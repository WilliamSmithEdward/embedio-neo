namespace EmbedIO.Quic
{
    internal enum QuicCipherSuite : ushort
    {
        Aes128GcmSha256 = 0x1301,
        Aes256GcmSha384 = 0x1302,
        ChaCha20Poly1305Sha256 = 0x1303
    }
}
