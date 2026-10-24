/* Original EmbedIO recordless TLS boundary. OpenSSL supplies TLS only. */
#include <stdint.h>
#include <stdatomic.h>
#include <stddef.h>
#include <stdlib.h>
#include <string.h>
#include <openssl/crypto.h>
#include <openssl/ssl.h>
#include <openssl/err.h>
#include <openssl/pkcs12.h>
#include <openssl/core_dispatch.h>

#define CHUNK_LIMIT 65536u
#define QUEUE_LIMIT 262144u
#define EVENT_LIMIT 64u

struct block {
    struct block *next;
    size_t length;
    int kind, level, cipher;
    unsigned char bytes[];
};
struct connection {
    SSL_CTX *configuration;
    SSL *tls;
    struct block *input[3], *tail[3], *held, *events, *last_event;
    size_t input_bytes, input_count, event_bytes, event_count;
    int read_level, write_level, credential_set, error, alert;
    unsigned char alpn[255], parameters[4096];
    size_t alpn_length, parameters_length;
};
/* A selected certificate's key, leaf and issuer chain, parsed once and shared by the
   connections that install it. Each connection takes its own OpenSSL references. The
   managed owner completes the issuer chain before it shares the credential; a refused
   issuer retires the credential, as it retired the connection in ABI 2. */
struct credential {
    EVP_PKEY *key;
    X509 *certificate;
    struct stack_st_X509 *chain; /* STACK_OF(X509), which the pinned Semgrep cannot parse here */
    int error;
};

static _Atomic uint64_t live_connections;
static _Atomic uint64_t live_blocks;
static _Atomic uint64_t live_credentials;

static void release_block(struct block *value)
{
    if (value != NULL) {
        atomic_fetch_sub_explicit(&live_blocks, 1, memory_order_relaxed);
        OPENSSL_clear_free(value, sizeof(*value) + value->length);
    }
}
static int fail(struct connection *owner) { owner->error = 1; return 0; }
static struct block *allocate_block(const unsigned char *bytes, size_t length)
{
    struct block *value;
    if (length > CHUNK_LIMIT || (length != 0 && bytes == NULL)) return NULL;
    value = OPENSSL_zalloc(sizeof(*value) + length);
    if (value != NULL) {
        atomic_fetch_add_explicit(&live_blocks, 1, memory_order_relaxed);
        value->length = length;
        if (length != 0) memcpy(value->bytes, bytes, length);
    }
    return value;
}
static int event(struct connection *owner, int kind, int level, int cipher,
                 const unsigned char *bytes, size_t length)
{
    struct block *value;
    if (owner->event_count == EVENT_LIMIT || length > QUEUE_LIMIT - owner->event_bytes)
        return fail(owner);
    value = allocate_block(bytes, length);
    if (value == NULL) return fail(owner);
    value->kind = kind; value->level = level; value->cipher = cipher;
    if (owner->last_event != NULL) owner->last_event->next = value;
    else owner->events = value;
    owner->last_event = value;
    owner->event_count++; owner->event_bytes += length;
    return 1;
}
static int send_crypto(SSL *tls, const unsigned char *bytes, size_t length,
                       size_t *used, void *argument)
{
    struct connection *owner = argument;
    (void)tls;
    *used = 0;
    if (!event(owner, 1, owner->write_level, 0, bytes, length)) return 0;
    *used = length;
    return 1;
}
static int receive_crypto(SSL *tls, const unsigned char **bytes, size_t *length, void *argument)
{
    struct connection *owner = argument;
    struct block *value;
    (void)tls;
    *bytes = NULL; *length = 0;
    if (owner->held != NULL || owner->read_level < 0 || owner->read_level > 2)
        return fail(owner);
    value = owner->input[owner->read_level];
    if (value != NULL) {
        owner->input[owner->read_level] = value->next;
        if (value->next == NULL) owner->tail[owner->read_level] = NULL;
        value->next = NULL; owner->held = value;
        *bytes = value->bytes; *length = value->length;
    }
    return 1;
}
static int release_crypto(SSL *tls, size_t length, void *argument)
{
    struct connection *owner = argument;
    (void)tls;
    if (owner->held == NULL || owner->held->length != length) return fail(owner);
    owner->input_bytes -= length; owner->input_count--;
    release_block(owner->held); owner->held = NULL;
    return 1;
}
static int yield_secret(SSL *tls, uint32_t protection, int direction,
                        const unsigned char *secret, size_t length, void *argument)
{
    struct connection *owner = argument;
    const SSL_CIPHER *selected = SSL_get_current_cipher(tls);
    int cipher, level;
    if (selected == NULL || protection < 2 || protection > 3 || direction < 0 || direction > 1)
        return fail(owner);
    cipher = SSL_CIPHER_get_protocol_id(selected);
    if (!((cipher == 0x1302 && length == 48)
            || ((cipher == 0x1301 || cipher == 0x1303) && length == 32))) return fail(owner);
    level = (int)protection - 1;
    if (direction == 0) owner->read_level = level;
    else owner->write_level = level;
    return event(owner, direction == 0 ? 2 : 3, level, cipher, secret, length);
}
static int got_parameters(SSL *tls, const unsigned char *bytes, size_t length, void *argument)
{
    struct connection *owner = argument;
    (void)tls;
    return length <= 4096 ? event(owner, 4, 0, 0, bytes, length) : fail(owner);
}
static int got_alert(SSL *tls, unsigned char alert, void *argument)
{
    struct connection *owner = argument;
    (void)tls;
    owner->alert = alert;
    return 1;
}
static int select_protocol(SSL *tls, const unsigned char **output, unsigned char *length,
                           const unsigned char *input, unsigned int size, void *argument)
{
    struct connection *owner = argument;
    size_t offset = 0;
    (void)tls;
    while (offset < size) {
        size_t candidate = input[offset++];
        if (candidate == 0 || candidate > size - offset) return SSL_TLSEXT_ERR_ALERT_FATAL;
        if (candidate == owner->alpn_length && memcmp(input + offset, owner->alpn, candidate) == 0) {
            *output = input + offset; *length = (unsigned char)candidate;
            return SSL_TLSEXT_ERR_OK;
        }
        offset += candidate;
    }
    return SSL_TLSEXT_ERR_ALERT_FATAL;
}
static int need_credential(SSL *tls, void *argument)
{
    struct connection *owner = argument;
    (void)tls;
    return owner->credential_set ? 1 : -1;
}
static int defer_client_validation(X509_STORE_CTX *chain, void *argument)
{
    X509 *certificate = X509_STORE_CTX_get0_cert(chain);
    (void)argument;
    /* Trust, usage, expiry and revocation belong to managed application policy.
       A key which cannot be decoded cannot prove possession in TLS, regardless
       of that policy. Reject it before native chain-building can fail internally. */
    if (certificate == NULL || X509_get0_pubkey(certificate) == NULL) {
        X509_STORE_CTX_set_error(chain, X509_V_ERR_UNABLE_TO_DECODE_ISSUER_PUBLIC_KEY);
        return 0;
    }
    X509_STORE_CTX_set_error(chain, X509_V_OK);
    return 1;
}
static const OSSL_DISPATCH dispatch[] = {
    { OSSL_FUNC_SSL_QUIC_TLS_CRYPTO_SEND, (void (*)(void))send_crypto },
    { OSSL_FUNC_SSL_QUIC_TLS_CRYPTO_RECV_RCD, (void (*)(void))receive_crypto },
    { OSSL_FUNC_SSL_QUIC_TLS_CRYPTO_RELEASE_RCD, (void (*)(void))release_crypto },
    { OSSL_FUNC_SSL_QUIC_TLS_YIELD_SECRET, (void (*)(void))yield_secret },
    { OSSL_FUNC_SSL_QUIC_TLS_GOT_TRANSPORT_PARAMS, (void (*)(void))got_parameters },
    { OSSL_FUNC_SSL_QUIC_TLS_ALERT, (void (*)(void))got_alert },
    { 0, NULL }
};

__attribute__((visibility("default"))) uint64_t embedio_quic_tls_connections(void) { return atomic_load_explicit(&live_connections, memory_order_relaxed); }
__attribute__((visibility("default"))) uint64_t embedio_quic_tls_blocks(void) { return atomic_load_explicit(&live_blocks, memory_order_relaxed); }
__attribute__((visibility("default"))) uint64_t embedio_quic_tls_credentials(void) { return atomic_load_explicit(&live_credentials, memory_order_relaxed); }
__attribute__((visibility("default"))) unsigned int embedio_quic_tls_abi(void) { return 3; }
__attribute__((visibility("default"))) const char *embedio_quic_tls_version(void) { return OpenSSL_version(OPENSSL_VERSION); }
__attribute__((visibility("default"))) void embedio_quic_tls_free(struct connection *owner)
{
    if (owner != NULL) {
        atomic_fetch_sub_explicit(&live_connections, 1, memory_order_relaxed);
        SSL_free(owner->tls);
        SSL_CTX_free(owner->configuration);
        release_block(owner->held);
        for (int level = 0; level < 3; level++) {
            while (owner->input[level] != NULL) {
                struct block *value = owner->input[level]; owner->input[level] = value->next;
                release_block(value);
            }
        }
        while (owner->events != NULL) {
            struct block *value = owner->events; owner->events = value->next;
            release_block(value);
        }
        OPENSSL_clear_free(owner, sizeof(*owner));
    }
}
__attribute__((visibility("default"))) struct connection *embedio_quic_tls_new(const unsigned char *alpn, size_t alpn_length,
        const unsigned char *parameters, size_t parameters_length, int client_certificate)
{
    struct connection *owner;
    if (alpn == NULL || alpn_length == 0 || alpn_length > 255 || parameters_length > 4096
            || (parameters_length != 0 && parameters == NULL)
            || client_certificate < 0 || client_certificate > 1) return NULL;
    owner = OPENSSL_zalloc(sizeof(*owner));
    if (owner == NULL) return NULL;
    atomic_fetch_add_explicit(&live_connections, 1, memory_order_relaxed);
    owner->alpn_length = alpn_length; memcpy(owner->alpn, alpn, alpn_length);
    owner->parameters_length = parameters_length;
    if (parameters_length != 0) memcpy(owner->parameters, parameters, parameters_length);
    owner->configuration = SSL_CTX_new(TLS_method());
    if (owner->configuration == NULL
            || !SSL_CTX_set_ciphersuites(owner->configuration,
                "TLS_AES_256_GCM_SHA384:TLS_AES_128_GCM_SHA256:TLS_CHACHA20_POLY1305_SHA256")) goto failure;
    SSL_CTX_set_options(owner->configuration, SSL_OP_NO_RENEGOTIATION);
    SSL_CTX_set_session_cache_mode(owner->configuration, SSL_SESS_CACHE_OFF);
    SSL_CTX_set_num_tickets(owner->configuration, 0);
    SSL_CTX_set_alpn_select_cb(owner->configuration, select_protocol, owner);
    SSL_CTX_set_cert_cb(owner->configuration, need_credential, owner);
    SSL_CTX_set_verify_depth(owner->configuration, 32);
    SSL_CTX_set_verify(owner->configuration, client_certificate ? SSL_VERIFY_PEER : SSL_VERIFY_NONE, NULL);
    SSL_CTX_set_cert_verify_callback(owner->configuration, defer_client_validation, NULL);
    owner->tls = SSL_new(owner->configuration);
    if (owner->tls == NULL) goto failure;
    SSL_set_accept_state(owner->tls);
    if (!SSL_set_quic_tls_cbs(owner->tls, dispatch, owner)
            || !SSL_set_quic_tls_transport_params(owner->tls, owner->parameters, parameters_length)) goto failure;
    return owner;
failure:
    embedio_quic_tls_free(owner);
    return NULL;
}
__attribute__((visibility("default"))) int embedio_quic_tls_add(struct connection *owner, int level, const unsigned char *data, size_t length)
{
    struct block *value;
    if (owner == NULL || owner->error || level < 0 || level > 2 || length == 0
            || owner->input_count >= EVENT_LIMIT || length > CHUNK_LIMIT
            || length > QUEUE_LIMIT - owner->input_bytes) return 0;
    value = allocate_block(data, length);
    if (value == NULL) return fail(owner);
    if (owner->tail[level] == NULL) owner->input[level] = value;
    else owner->tail[level]->next = value;
    owner->tail[level] = value; owner->input_bytes += length; owner->input_count++;
    return 1;
}
__attribute__((visibility("default"))) int embedio_quic_tls_step(struct connection *owner)
{
    int result, error;
    if (owner == NULL || owner->error) return -1;
    ERR_clear_error();
    if (SSL_is_init_finished(owner->tls)) {
        unsigned char unexpected;
        size_t received = 0;
        result = SSL_read_ex(owner->tls, &unexpected, 1, &received);
        if (received != 0 || result == 1) { fail(owner); return -1; }
    } else {
        result = SSL_do_handshake(owner->tls);
        if (result == 1) return 1;
    }
    error = SSL_get_error(owner->tls, result);
    if (owner->error) return -1;
    if (error == SSL_ERROR_WANT_X509_LOOKUP) return 2;
    if (error == SSL_ERROR_WANT_READ) return SSL_is_init_finished(owner->tls) ? 1 : 0;
    fail(owner);
    return -1;
}
__attribute__((visibility("default"))) int embedio_quic_tls_server_name(struct connection *owner, unsigned char *output, size_t capacity)
{
    const char *name;
    size_t length;
    if (owner == NULL) return -1;
    name = SSL_get_servername(owner->tls, TLSEXT_NAMETYPE_host_name);
    if (name == NULL) return 0;
    length = strlen(name);
    if (length > 255 || length > capacity || output == NULL) return -1;
    memcpy(output, name, length);
    return (int)length;
}
__attribute__((visibility("default"))) void embedio_quic_tls_credential_free(struct credential *credential)
{
    if (credential != NULL) {
        atomic_fetch_sub_explicit(&live_credentials, 1, memory_order_relaxed);
        EVP_PKEY_free(credential->key);
        X509_free(credential->certificate);
        sk_X509_pop_free(credential->chain, X509_free);
        OPENSSL_clear_free(credential, sizeof(*credential));
    }
}
/* Parse the selected certificate's PKCS#12 export, protected by an empty password. */
__attribute__((visibility("default"))) struct credential *embedio_quic_tls_credential_new(const unsigned char *data, size_t length)
{
    const unsigned char *cursor = data;
    PKCS12 *encoded;
    struct credential *credential;
    int parsed;
    if (data == NULL || length == 0 || length > 1048576) return NULL;
    credential = OPENSSL_zalloc(sizeof(*credential));
    if (credential == NULL) return NULL;
    atomic_fetch_add_explicit(&live_credentials, 1, memory_order_relaxed);
    encoded = d2i_PKCS12(NULL, &cursor, (long)length);
    parsed = encoded != NULL && cursor == data + length
        && PKCS12_parse(encoded, "", &credential->key, &credential->certificate, &credential->chain);
    PKCS12_free(encoded);
    if (!parsed || credential->key == NULL || credential->certificate == NULL
            || !X509_check_private_key(credential->certificate, credential->key)
            || sk_X509_num(credential->chain) > 32
            || (credential->chain == NULL && (credential->chain = sk_X509_new_null()) == NULL)) {
        embedio_quic_tls_credential_free(credential);
        return NULL;
    }
    return credential;
}
/* Append public issuer certificates in managed chain order. Only before the credential is
   shared: use_credential duplicates this stack without a lock. */
__attribute__((visibility("default"))) int embedio_quic_tls_credential_add_chain(struct credential *credential,
        const unsigned char *data, size_t length)
{
    const unsigned char *cursor = data;
    X509 *certificate;
    if (credential == NULL || credential->error || sk_X509_num(credential->chain) >= 32
            || data == NULL || length == 0 || length > CHUNK_LIMIT) return 0;
    certificate = d2i_X509(NULL, &cursor, (long)length);
    if (certificate == NULL || cursor != data + length || !sk_X509_push(credential->chain, certificate)) {
        X509_free(certificate);
        credential->error = 1;
        return 0;
    }
    return 1;
}
/* Install a credential for this connection's handshake. The connection holds its own
   references afterward, so the owner may free the credential while TLS continues. */
__attribute__((visibility("default"))) int embedio_quic_tls_use_credential(struct connection *owner,
        const struct credential *credential)
{
    if (owner == NULL || owner->credential_set || owner->error) return 0;
    if (credential == NULL || credential->error
            || !SSL_use_certificate(owner->tls, credential->certificate)
            || !SSL_use_PrivateKey(owner->tls, credential->key) || !SSL_check_private_key(owner->tls)
            || !SSL_set1_chain(owner->tls, credential->chain)) return fail(owner);
    owner->credential_set = 1;
    return 1;
}
__attribute__((visibility("default"))) int embedio_quic_tls_next(struct connection *owner, int *kind, int *level, int *cipher,
        unsigned char *output, size_t capacity, size_t *length)
{
    struct block *value;
    if (owner == NULL || kind == NULL || level == NULL || cipher == NULL || length == NULL) return -1;
    value = owner->events;
    *length = 0;
    if (value == NULL) return 0;
    *kind = value->kind; *level = value->level; *cipher = value->cipher; *length = value->length;
    if (value->length > capacity || output == NULL) return 2;
    memcpy(output, value->bytes, value->length);
    owner->events = value->next;
    if (value->next == NULL) owner->last_event = NULL;
    owner->event_bytes -= value->length; owner->event_count--;
    release_block(value);
    return 1;
}
__attribute__((visibility("default"))) int embedio_quic_tls_alert(struct connection *owner) { return owner == NULL ? 0 : owner->alert; }

/* Borrowed native certificates are serialized only while this TLS owner is live.
   Managed policy receives independent DER copies after Finished validation. */
__attribute__((visibility("default"))) int embedio_quic_tls_certificate_count(struct connection *owner)
{
    X509 *leaf;
    STACK_OF(X509) *chain;
    int additional;
    if (owner == NULL || owner->error || !SSL_is_init_finished(owner->tls)) return -1;
    leaf = SSL_get0_peer_certificate(owner->tls);
    chain = SSL_get_peer_cert_chain(owner->tls);
    additional = sk_X509_num(chain);
    if (additional < 0) additional = 0;
    if (leaf == NULL) return additional == 0 ? 0 : -1;
    /* A TLS Certificate entry uses at least its length and extension framing.
       The input is bounded by the same 64KiB handshake reassembly budget. */
    if (additional > (int)CHUNK_LIMIT) return -1;
    return additional + 1;
}
__attribute__((visibility("default"))) int embedio_quic_tls_certificate(struct connection *owner,
        int index, unsigned char *output, size_t capacity)
{
    X509 *certificate;
    unsigned char *cursor = output;
    int count = embedio_quic_tls_certificate_count(owner), length, written;
    if (index < 0 || index >= count) return -1;
    certificate = index == 0 ? SSL_get0_peer_certificate(owner->tls)
        : sk_X509_value(SSL_get_peer_cert_chain(owner->tls), index - 1);
    if (certificate == NULL) return -1;
    length = i2d_X509(certificate, NULL);
    if (length <= 0 || length > (int)CHUNK_LIMIT) return -1;
    if (output == NULL) return length;
    if (capacity < (size_t)length) return -1;
    written = i2d_X509(certificate, &cursor);
    return written == length && cursor == output + length ? length : -1;
}
