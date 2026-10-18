using System.Runtime.CompilerServices;
using EmbedIO.Utilities;

namespace EmbedIO.Internal
{
    internal sealed class DummyWebModuleContainer : IWebModuleContainer
    {
        public static readonly IWebModuleContainer Instance = new DummyWebModuleContainer();

        private DummyWebModuleContainer() { }

        public IComponentCollection<IWebModule> Modules => throw UnexpectedCall();

        public void Dispose() { }

        private EmbedIOInternalErrorException UnexpectedCall([CallerMemberName] string member = "") =>
            SelfCheck.Failure($"Unexpected call to {nameof(DummyWebModuleContainer)}.{member}.");
    }
}
