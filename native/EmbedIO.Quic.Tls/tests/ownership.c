#include <assert.h>
#include <stdint.h>
#include <stddef.h>
#include <stdio.h>
#include <openssl/crypto.h>
struct connection;
struct credential;
extern unsigned int embedio_quic_tls_abi(void);
extern uint64_t embedio_quic_tls_connections(void);
extern uint64_t embedio_quic_tls_blocks(void);
extern uint64_t embedio_quic_tls_credentials(void);
extern struct connection *embedio_quic_tls_new(const unsigned char *, size_t, const unsigned char *, size_t, int);
extern int embedio_quic_tls_add(struct connection *, int, const unsigned char *, size_t);
extern int embedio_quic_tls_step(struct connection *);
extern void embedio_quic_tls_free(struct connection *);
extern int embedio_quic_tls_certificate_count(struct connection *);
extern int embedio_quic_tls_certificate(struct connection *, int, unsigned char *, size_t);
extern struct credential *embedio_quic_tls_credential_new(const unsigned char *, size_t);
extern int embedio_quic_tls_credential_add_chain(struct credential *, const unsigned char *, size_t);
extern void embedio_quic_tls_credential_free(struct credential *);
extern int embedio_quic_tls_use_credential(struct connection *, const struct credential *);
extern int embedio_quic_tls_set_ticket_keys(struct connection *, const unsigned char *, size_t, uint32_t);
static int released(void)
{
    return embedio_quic_tls_connections() == 0 && embedio_quic_tls_blocks() == 0
        && embedio_quic_tls_credentials() == 0;
}
static unsigned char *read_fixture(const char *path, size_t *length)
{
    FILE *input = fopen(path, "rb");
    long size;
    unsigned char *bytes;
    assert(input != NULL && fseek(input, 0, SEEK_END) == 0);
    size = ftell(input);
    assert(size > 0 && size <= 1048576 && fseek(input, 0, SEEK_SET) == 0);
    *length = (size_t)size;
    bytes = OPENSSL_malloc(*length);
    assert(bytes != NULL && fread(bytes, 1, *length, input) == *length);
    assert(fclose(input) == 0);
    return bytes;
}
int main(int argc, char **argv)
{
    const unsigned char protocol[] = {'h', '3'}, parameters[] = {1, 0}, invalid[] = {1, 2, 3};
    unsigned char keys[5 * 80] = {0};
    size_t credential_length, certificate_length;
    unsigned char *credential, *certificate;
    assert(argc == 3);
    credential = read_fixture(argv[1], &credential_length);
    certificate = read_fixture(argv[2], &certificate_length);
    assert(embedio_quic_tls_abi() == 4);
    assert(released());
    /* Ticket keys: whole 80-byte keys, at most four, a lifetime of one second to seven days,
       once per connection and before its first step. */
    assert(embedio_quic_tls_set_ticket_keys(NULL, keys, 80, 7200) == 0);
    {
        struct connection *owner = embedio_quic_tls_new(protocol, 2, parameters, 2, 0);
        assert(owner != NULL);
        assert(embedio_quic_tls_set_ticket_keys(owner, NULL, 80, 7200) == 0);
        assert(embedio_quic_tls_set_ticket_keys(owner, keys, 0, 7200) == 0);
        assert(embedio_quic_tls_set_ticket_keys(owner, keys, 79, 7200) == 0);
        assert(embedio_quic_tls_set_ticket_keys(owner, keys, 5 * 80, 7200) == 0);
        assert(embedio_quic_tls_set_ticket_keys(owner, keys, 80, 0) == 0);
        assert(embedio_quic_tls_set_ticket_keys(owner, keys, 80, 604801) == 0);
        assert(embedio_quic_tls_set_ticket_keys(owner, keys, 4 * 80, 604800) == 1);
        assert(embedio_quic_tls_set_ticket_keys(owner, keys, 80, 7200) == 0);
        assert(embedio_quic_tls_step(owner) == 0);
        embedio_quic_tls_free(owner);
        owner = embedio_quic_tls_new(protocol, 2, parameters, 2, 0);
        assert(owner != NULL && embedio_quic_tls_step(owner) == 0);
        assert(embedio_quic_tls_set_ticket_keys(owner, keys, 80, 7200) == 0);
        embedio_quic_tls_free(owner);
    }
    assert(released());
    assert(embedio_quic_tls_new(NULL, 2, parameters, 2, 0) == NULL);
    assert(embedio_quic_tls_new(protocol, 256, parameters, 2, 0) == NULL);
    assert(embedio_quic_tls_new(protocol, 2, NULL, 1, 0) == NULL);
    assert(embedio_quic_tls_new(protocol, 2, parameters, 4097, 0) == NULL);
    assert(embedio_quic_tls_certificate_count(NULL) == -1);
    assert(embedio_quic_tls_certificate(NULL, 0, NULL, 0) == -1);
    assert(embedio_quic_tls_new(protocol, 2, parameters, 2, -1) == NULL);
    assert(embedio_quic_tls_new(protocol, 2, parameters, 2, 2) == NULL);
    assert(embedio_quic_tls_credential_new(NULL, 1) == NULL);
    assert(embedio_quic_tls_credential_new(credential, 0) == NULL);
    assert(embedio_quic_tls_credential_new(credential, 1048577) == NULL);
    assert(embedio_quic_tls_credential_add_chain(NULL, certificate, certificate_length) == 0);
    assert(embedio_quic_tls_use_credential(NULL, NULL) == 0);
    embedio_quic_tls_credential_free(NULL);
    assert(released());
    for (int iteration = 0; iteration < 1024; iteration++) {
        struct credential *shared, *refused;
        struct connection *users[4];
        struct connection *owner = embedio_quic_tls_new(protocol, 2, parameters, 2, 0);
        assert(owner != NULL && embedio_quic_tls_connections() == 1);
        assert(embedio_quic_tls_certificate_count(owner) == -1);
        assert(embedio_quic_tls_certificate(owner, 0, NULL, 0) == -1);
        assert(embedio_quic_tls_step(owner) == 0);
        assert(!embedio_quic_tls_add(owner, -1, invalid, sizeof(invalid)));
        assert(!embedio_quic_tls_add(owner, 0, NULL, 1));
        assert(embedio_quic_tls_step(owner) == -1);
        embedio_quic_tls_free(owner);
        assert(embedio_quic_tls_connections() == 0 && embedio_quic_tls_blocks() == 0);
        owner = embedio_quic_tls_new(protocol, 2, parameters, 2, 0);
        assert(owner != NULL);
        for (int chunk = 0; chunk < 64; chunk++)
            assert(embedio_quic_tls_add(owner, 2, invalid, sizeof(invalid)) == 1);
        assert(embedio_quic_tls_blocks() == 64);
        assert(!embedio_quic_tls_add(owner, 2, invalid, 1));
        embedio_quic_tls_free(owner);
        assert(embedio_quic_tls_connections() == 0 && embedio_quic_tls_blocks() == 0);
        assert(embedio_quic_tls_credential_new(invalid, sizeof(invalid)) == NULL && released());
        /* One credential with 32 issuers serves several connections and is freed first. */
        shared = embedio_quic_tls_credential_new(credential, credential_length);
        assert(shared != NULL && embedio_quic_tls_credentials() == 1);
        assert(!embedio_quic_tls_credential_add_chain(shared, NULL, 1));
        assert(!embedio_quic_tls_credential_add_chain(shared, certificate, 65537));
        for (int index = 0; index < 32; index++)
            assert(embedio_quic_tls_credential_add_chain(shared, certificate, certificate_length) == 1);
        assert(!embedio_quic_tls_credential_add_chain(shared, certificate, certificate_length));
        for (int user = 0; user < 4; user++) {
            users[user] = embedio_quic_tls_new(protocol, 2, parameters, 2, 0);
            assert(users[user] != NULL && embedio_quic_tls_use_credential(users[user], shared) == 1);
            assert(!embedio_quic_tls_use_credential(users[user], shared));
        }
        embedio_quic_tls_credential_free(shared);
        assert(embedio_quic_tls_credentials() == 0 && embedio_quic_tls_connections() == 4);
        for (int user = 0; user < 4; user++) {
            assert(embedio_quic_tls_step(users[user]) == 0);
            embedio_quic_tls_free(users[user]);
        }
        assert(released());
        /* A malformed issuer retires its credential, and installing it retires the connection. */
        refused = embedio_quic_tls_credential_new(credential, credential_length);
        assert(refused != NULL && !embedio_quic_tls_credential_add_chain(refused, invalid, sizeof(invalid)));
        assert(!embedio_quic_tls_credential_add_chain(refused, certificate, certificate_length));
        owner = embedio_quic_tls_new(protocol, 2, parameters, 2, 0);
        assert(owner != NULL && !embedio_quic_tls_use_credential(owner, refused));
        assert(embedio_quic_tls_step(owner) == -1);
        embedio_quic_tls_free(owner);
        embedio_quic_tls_credential_free(refused);
        assert(released());
        owner = embedio_quic_tls_new(protocol, 2, parameters, 2, 0);
        assert(owner != NULL && !embedio_quic_tls_use_credential(owner, NULL));
        assert(embedio_quic_tls_step(owner) == -1);
        embedio_quic_tls_free(owner);
        assert(released());
    }
    embedio_quic_tls_free(NULL);
    OPENSSL_clear_free(credential, credential_length);
    OPENSSL_clear_free(certificate, certificate_length);
    OPENSSL_cleanup();
    puts("{\"passed\":true,\"lifecycles\":8192,\"credentialLifecycles\":3072,\"queuedBlocksPerCase\":64,\"connectionsPerCredential\":4,\"contextsRetained\":0,\"blocksRetained\":0,\"credentialsRetained\":0}");
    return 0;
}
