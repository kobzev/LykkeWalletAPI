using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Core.Identity;
using IdentityModel.AspNetCore.OAuth2Introspection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LykkeApi2.Infrastructure.Authentication
{
    internal class LykkeAuthHandler : OAuth2IntrospectionHandler
    {
        private readonly ILykkePrincipal _lykkePrincipal;
        private const int LykkeTokenLength = 64;
        private static IDistributedCache _cache;


        public LykkeAuthHandler(IOptionsMonitor<OAuth2IntrospectionOptions> options, ILoggerFactory logger, UrlEncoder encoder, ISystemClock clock, ILykkePrincipal lykkePrincipal)
            : base(options, encoder, clock, logger, GetInMemoryCache())
        {
            _lykkePrincipal = lykkePrincipal;
        }

        // We don't want to use redis
        private static IDistributedCache GetInMemoryCache()
        {
            if (_cache == null)
            {
                _cache = new MemoryDistributedCache(new OptionsWrapper<MemoryDistributedCacheOptions>(new MemoryDistributedCacheOptions()));
            }

            return _cache;
        }

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var token = TokenRetrieval.FromAuthorizationHeader()(Context.Request);
            if (token == null)
            {
                return AuthenticateResult.NoResult();
            }

            if (token.Length != LykkeTokenLength)
            {
                var result = await base.HandleAuthenticateAsync();
                return result;
            }

            var principal = await _lykkePrincipal.GetCurrent();

            if (principal == null)
                return AuthenticateResult.NoResult();

            var ticket = new AuthenticationTicket(principal, "Bearer");

            return AuthenticateResult.Success(ticket);
        }
    }
}