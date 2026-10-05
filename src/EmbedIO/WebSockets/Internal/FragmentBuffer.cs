using System.IO;

namespace EmbedIO.WebSockets.Internal
{
    internal class FragmentBuffer : MemoryStream
    {
        private readonly Opcode _fragmentsOpcode;

        public FragmentBuffer(Opcode frameOpcode)
        {
            _fragmentsOpcode = frameOpcode;
        }

        public void AddPayload(byte[] data) => Write(data, 0, data.Length);

        public MessageEventArgs GetMessage() => new MessageEventArgs(_fragmentsOpcode, ToArray());
    }
}
