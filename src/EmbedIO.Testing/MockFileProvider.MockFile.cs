using System;

namespace EmbedIO.Testing
{
    partial class MockFileProvider
    {
        private sealed class MockFile(byte[] data) : MockDirectoryEntry
        {
            public byte[] Data { get; private set; } = data ?? [];

            public void SetData(byte[] data)
            {
                Data = data ?? [];
                Touch();
            }
        }
    }
}
