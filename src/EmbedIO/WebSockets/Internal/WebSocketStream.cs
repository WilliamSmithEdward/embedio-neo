using System;
using System.Collections.Generic;
using System.IO;

namespace EmbedIO.WebSockets.Internal
{
    internal class WebSocketStream : MemoryStream
    {
        internal const int FragmentLength = 1016;

        private readonly Opcode _opcode;

        public WebSocketStream(byte[] data, Opcode opcode)
            : base(data)
        {
            _opcode = opcode;
        }

        public IEnumerable<WebSocketFrame> GetFrames()
        {
            var len = Length;

            /* Not fragmented */

            if (len == 0)
            {
                yield return new WebSocketFrame(Fin.Final, _opcode, Array.Empty<byte>(), false);
                yield break;
            }

            var quo = len / FragmentLength;
            var rem = (int)(len % FragmentLength);

            byte[] buff;

            if (quo == 0)
            {
                buff = new byte[rem];

                if (Read(buff, 0, rem) == rem)
                    yield return new WebSocketFrame(Fin.Final, _opcode, buff, false);

                yield break;
            }

            buff = new byte[FragmentLength];
            if (quo == 1 && rem == 0)
            {
                if (Read(buff, 0, FragmentLength) == FragmentLength)
                    yield return new WebSocketFrame(Fin.Final, _opcode, buff, false);

                yield break;
            }

            /* Send fragmented */

            // Begin
            if (Read(buff, 0, FragmentLength) != FragmentLength)
                yield break;

            yield return new WebSocketFrame(Fin.More, _opcode, buff, false);

            var n = rem == 0 ? quo - 2 : quo - 1;
            for (var i = 0; i < n; i++)
            {
                if (Read(buff, 0, FragmentLength) != FragmentLength)
                    yield break;

                yield return new WebSocketFrame(Fin.More, Opcode.Cont, buff, false);
            }

            // End
            if (rem == 0)
                rem = FragmentLength;
            else
                buff = new byte[rem];

            if (Read(buff, 0, rem) == rem)
                yield return new WebSocketFrame(Fin.Final, Opcode.Cont, buff, false);
        }
    }
}
