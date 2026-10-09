using System;
using System.Collections.Generic;

namespace EmbedIO.WebSockets.Internal
{
    // Keeps each fragment's payload array and copies once when the message
    // completes, instead of growing and trimming an intermediate stream.
    internal sealed class FragmentBuffer : IDisposable
    {
        private readonly Opcode _fragmentsOpcode;
        private readonly List<byte[]> _fragments = new();
        private long _length;

        public FragmentBuffer(Opcode frameOpcode)
        {
            _fragmentsOpcode = frameOpcode;
        }

        public void AddPayload(byte[] data)
        {
            if (data.Length == 0) return;
            _fragments.Add(data);
            _length += data.Length;
        }

        public MessageEventArgs GetMessage()
        {
            if (_fragments.Count == 1) return new MessageEventArgs(_fragmentsOpcode, _fragments[0]);
            var message = new byte[checked((int)_length)];
            var offset = 0;
            foreach (var fragment in _fragments)
            {
                Buffer.BlockCopy(fragment, 0, message, offset, fragment.Length);
                offset += fragment.Length;
            }
            return new MessageEventArgs(_fragmentsOpcode, message);
        }

        public void Dispose()
        {
            _fragments.Clear();
            _length = 0;
        }
    }
}
