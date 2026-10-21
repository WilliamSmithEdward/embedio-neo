#include <assert.h>
#include <stdint.h>
#include <stddef.h>
#include <stdio.h>
#include <openssl/crypto.h>
struct connection;
extern unsigned int embedio_quic_tls_abi(void);
extern uint64_t embedio_quic_tls_connections(void);
extern uint64_t embedio_quic_tls_blocks(void);
extern struct connection *embedio_quic_tls_new(const unsigned char *, size_t, const unsigned char *, size_t, int);
extern int embedio_quic_tls_add(struct connection *, int, const unsigned char *, size_t);
extern int embedio_quic_tls_step(struct connection *);
extern int embedio_quic_tls_set_credential(struct connection *, const unsigned char *, size_t);
extern void embedio_quic_tls_free(struct connection *);
extern int embedio_quic_tls_certificate_count(struct connection *);
extern int embedio_quic_tls_certificate(struct connection *, int, unsigned char *, size_t);
int main(void)
{
    const unsigned char protocol[] = {'h', '3'}, parameters[] = {1, 0}, invalid[] = {1, 2, 3};
    assert(embedio_quic_tls_abi() == 1);
    assert(embedio_quic_tls_connections() == 0 && embedio_quic_tls_blocks() == 0);
    assert(embedio_quic_tls_new(NULL, 2, parameters, 2, 0) == NULL);
    assert(embedio_quic_tls_new(protocol, 256, parameters, 2, 0) == NULL);
    assert(embedio_quic_tls_new(protocol, 2, NULL, 1, 0) == NULL);
    assert(embedio_quic_tls_new(protocol, 2, parameters, 4097, 0) == NULL);
    assert(embedio_quic_tls_certificate_count(NULL) == -1);
    assert(embedio_quic_tls_certificate(NULL, 0, NULL, 0) == -1);
    assert(embedio_quic_tls_new(protocol, 2, parameters, 2, -1) == NULL);
    assert(embedio_quic_tls_new(protocol, 2, parameters, 2, 2) == NULL);
    for (int iteration = 0; iteration < 1024; iteration++) {
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
        owner = embedio_quic_tls_new(protocol, 2, parameters, 2, 0);
        assert(owner != NULL);
        assert(!embedio_quic_tls_set_credential(owner, invalid, sizeof(invalid)));
        assert(embedio_quic_tls_step(owner) == -1);
        embedio_quic_tls_free(owner);
        assert(embedio_quic_tls_connections() == 0 && embedio_quic_tls_blocks() == 0);
    }
    embedio_quic_tls_free(NULL);
    OPENSSL_cleanup();
    puts("{\"passed\":true,\"lifecycles\":3072,\"queuedBlocksPerCase\":64,\"contextsRetained\":0,\"blocksRetained\":0}");
    return 0;
}
