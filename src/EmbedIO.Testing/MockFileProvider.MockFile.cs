using System;

namespace EmbedIO.Testing
{
    partial class MockFileProvider
    {
        private sealed class MockFile : MockDirectoryEntry
        {
            public MockFile(byte[] data)
            {
                Data = data ?? Array.Empty<byte>();
            }

            public byte[] Data { get; private set; }

            public void SetData(byte[] data)
            {
                Data = data ?? Array.Empty<byte>();
                Touch();
            }
        }
    }
}
