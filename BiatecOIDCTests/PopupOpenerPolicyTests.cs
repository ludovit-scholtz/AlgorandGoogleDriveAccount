using BiatecOIDC.Helper;
using Microsoft.AspNetCore.Http;

namespace BiatecOIDCTests
{
    [TestFixture]
    public class PopupOpenerPolicyTests
    {
        private static async Task<string?> HeaderFor(string path, bool enabled)
        {
            var context = new DefaultHttpContext();
            context.Request.Path = path;
            PopupOpenerPolicy.Apply(context, enabled);
            await Task.CompletedTask;
            return context.Response.Headers.TryGetValue(PopupOpenerPolicy.HeaderName, out var v) ? v.ToString() : null;
        }

        [TestCase("/authorize")]
        [TestCase("/authorize/consent")]
        [TestCase("/authorize/callback")]
        [TestCase("/select-provider")]
        [TestCase("/logout")]
        [TestCase("/connect/endsession")]
        [TestCase("/AUTHORIZE/consent")]
        public async Task PopupPages_GetTheHeader_WhenEnabled(string path)
            => Assert.That(await HeaderFor(path, true), Is.EqualTo("same-origin"));

        [TestCase("/token")]
        [TestCase("/userinfo")]
        [TestCase("/.well-known/openid-configuration")]
        [TestCase("/wallet/algorand-testnet/ADDR/sign")]
        [TestCase("/authorized-clients")]
        [TestCase("/")]
        public async Task OtherPaths_NeverGetTheHeader(string path)
            => Assert.That(await HeaderFor(path, true), Is.Null);

        [TestCase("/authorize")]
        [TestCase("/logout")]
        public async Task DisabledByDefault_NoPageGetsTheHeader(string path)
            => Assert.That(await HeaderFor(path, false), Is.Null);
    }
}
