// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.Owin.Infrastructure;
using Microsoft.Owin.Logging;
using Microsoft.Owin.Security.Infrastructure;

namespace Microsoft.Owin.Security.Cookies
{
    internal class CookieAuthenticationHandler : AuthenticationHandler<CookieAuthenticationOptions>
    {
        private const string HeaderNameCacheControl = "Cache-Control";
        private const string HeaderNamePragma = "Pragma";
        private const string HeaderNameExpires = "Expires";
        private const string HeaderValueNoCache = "no-cache";
        private const string HeaderValueMinusOne = "-1";
        private const string SessionIdClaim = "Microsoft.Owin.Security.Cookies-SessionId";

        private readonly ILogger _logger;

        private bool _shouldRenew;
        private DateTimeOffset _renewIssuedUtc;
        private DateTimeOffset _renewExpiresUtc;
        private string _sessionKey;

        public CookieAuthenticationHandler(ILogger logger)
        {
            if (logger == null)
            {
                throw new ArgumentNullException("logger");
            }
            _logger = logger;
        }

        protected override async Task<AuthenticationTicket> AuthenticateCoreAsync()
        {
            AuthenticationTicket ticket = null;
            try
            {
                string cookie = Options.CookieManager.GetRequestCookie(Context, Options.CookieName);
                if (string.IsNullOrWhiteSpace(cookie))
                {
                    return null;
                }

                ticket = Options.TicketDataFormat.Unprotect(cookie);

                if (ticket == null)
                {
                    _logger.WriteWarning(@"Unprotect ticket failed");
                    return null;
                }

                if (Options.SessionStore != null)
                {
                    Claim claim = ticket.Identity.Claims.FirstOrDefault(c => c.Type.Equals(SessionIdClaim));
                    if (claim == null)
                    {
                        _logger.WriteWarning(@"SessionId missing");
                        return null;
                    }
                    _sessionKey = claim.Value;
                    ticket = await Options.SessionStore.RetrieveAsync(_sessionKey);
                    if (ticket == null)
                    {
                        _logger.WriteWarning(@"Identity missing in session store");
                        return null;
                    }
                }

                DateTimeOffset currentUtc = Options.SystemClock.UtcNow;
                DateTimeOffset? issuedUtc = ticket.Properties.IssuedUtc;
                DateTimeOffset? expiresUtc = ticket.Properties.ExpiresUtc;

                if (expiresUtc != null && expiresUtc.Value < currentUtc)
                {
                    if (Options.SessionStore != null)
                    {
                        await Options.SessionStore.RemoveAsync(_sessionKey);
                    }
                    return null;
                }

                bool? allowRefresh = ticket.Properties.AllowRefresh;
                if (issuedUtc != null && expiresUtc != null && Options.SlidingExpiration
                    && (!allowRefresh.HasValue || allowRefresh.Value))
                {
                    TimeSpan timeElapsed = currentUtc.Subtract(issuedUtc.Value);
                    TimeSpan timeRemaining = expiresUtc.Value.Subtract(currentUtc);

                    if (timeRemaining < timeElapsed)
                    {
                        _shouldRenew = true;
                        _renewIssuedUtc = currentUtc;
                        TimeSpan timeSpan = expiresUtc.Value.Subtract(issuedUtc.Value);
                        _renewExpiresUtc = currentUtc.Add(timeSpan);
                    }
                }

                var context = new CookieValidateIdentityContext(Context, ticket, Options);

                await Options.Provider.ValidateIdentity(context);

                if (context.Identity == null)
                {
                    // Rejected
                    _shouldRenew = false;
                    return null;
                }

                return new AuthenticationTicket(context.Identity, context.Properties);
            }
            catch (Exception exception)
            {
                CookieExceptionContext exceptionContext = new CookieExceptionContext(Context, Options,
                    CookieExceptionContext.ExceptionLocation.AuthenticateAsync, exception, ticket);
                Options.Provider.Exception(exceptionContext);
                if (exceptionContext.Rethrow)
                {
                    throw;
                }
                return exceptionContext.Ticket;
            }
        }

        protected override async Task ApplyResponseGrantAsync()
        {
            AuthenticationResponseGrant signin = Helper.LookupSignIn(Options.AuthenticationType);
            bool shouldSignin = signin != null;
            AuthenticationResponseRevoke signout = Helper.LookupSignOut(Options.AuthenticationType, Options.AuthenticationMode);
            bool shouldSignout = signout != null;

            if (!(shouldSignin || shouldSignout || _shouldRenew))
            {
                return;
            }

            AuthenticationTicket model = await AuthenticateAsync();
            try
            {
                var cookieOptions = new CookieOptions
                {
                    Domain = Options.CookieDomain,
                    HttpOnly = Options.CookieHttpOnly,
                    SameSite = Options.CookieSameSite,
                    Path = Options.CookiePath ?? "/",
                };
                if (Options.CookieSecure == CookieSecureOption.SameAsRequest)
                {
                    cookieOptions.Secure = Request.IsSecure;
                }
                else
                {
                    cookieOptions.Secure = Options.CookieSecure == CookieSecureOption.Always;
                }

                if (shouldSignin)
                {
                    var signInContext = new CookieResponseSignInContext(
                        Context,
                        Options,
                        Options.AuthenticationType,
                        signin.Identity,
                        signin.Properties,
                        cookieOptions);

                    DateTimeOffset issuedUtc;
                    if (signInContext.Properties.IssuedUtc.HasValue)
                    {
                        issuedUtc = signInContext.Properties.IssuedUtc.Value;
                    }
                    else
                    {
                        issuedUtc = Options.SystemClock.UtcNow;
                        signInContext.Properties.IssuedUtc = issuedUtc;
                    }

                    if (!signInContext.Properties.ExpiresUtc.HasValue)
                    {
                        signInContext.Properties.ExpiresUtc = issuedUtc.Add(Options.ExpireTimeSpan);
                    }

                    Options.Provider.ResponseSignIn(signInContext);

                    if (signInContext.Properties.IsPersistent)
                    {
                        DateTimeOffset expiresUtc = signInContext.Properties.ExpiresUtc ?? issuedUtc.Add(Options.ExpireTimeSpan);
                        signInContext.CookieOptions.Expires = expiresUtc.UtcDateTime;
                    }

                    model = new AuthenticationTicket(signInContext.Identity, signInContext.Properties);
                    if (Options.SessionStore != null)
                    {
                        if (_sessionKey != null)
                        {
                            await Options.SessionStore.RemoveAsync(_sessionKey);
                        }
                        _sessionKey = await Options.SessionStore.StoreAsync(model);
                        ClaimsIdentity identity = new ClaimsIdentity(
                            new[] { new Claim(SessionIdClaim, _sessionKey) },
                            Options.AuthenticationType);
                        model = new AuthenticationTicket(identity, null);
                    }

                    string cookieValue = Options.TicketDataFormat.Protect(model);

                    Options.CookieManager.AppendResponseCookie(
                        Context,
                        Options.CookieName,
                        cookieValue,
                        signInContext.CookieOptions);

                    var signedInContext = new CookieResponseSignedInContext(
                        Context,
                        Options,
                        Options.AuthenticationType,
                        signInContext.Identity,
                        signInContext.Properties);

                    Options.Provider.ResponseSignedIn(signedInContext);
                }
                else if (shouldSignout)
                {
                    if (Options.SessionStore != null && _sessionKey != null)
                    {
                        await Options.SessionStore.RemoveAsync(_sessionKey);
                    }

                    var context = new CookieResponseSignOutContext(
                        Context,
                        Options,
                        cookieOptions);
                    
                    Options.Provider.ResponseSignOut(context);

                    Options.CookieManager.DeleteCookie(
                        Context,
                        Options.CookieName,
                        context.CookieOptions);
                }
                else if (_shouldRenew)
                {
                    var properties = model.Properties;

                    properties.IssuedUtc = _renewIssuedUtc;
                    properties.ExpiresUtc = _renewExpiresUtc;

                    if (Options.SessionStore != null && _sessionKey != null)
                    {
                        await Options.SessionStore.RenewAsync(_sessionKey, model);
                        ClaimsIdentity identity = new ClaimsIdentity(
                            new[] { new Claim(SessionIdClaim, _sessionKey) },
                            Options.AuthenticationType);
                        model = new AuthenticationTicket(identity, null);
                    }

                    string cookieValue = Options.TicketDataFormat.Protect(model);

                    // Check the non-SessionStore properties
                    if (properties.IsPersistent)
                    {
                        cookieOptions.Expires = _renewExpiresUtc.UtcDateTime;
                    }

                    Options.CookieManager.AppendResponseCookie(
                        Context,
                        Options.CookieName,
                        cookieValue,
                        cookieOptions);
                }

                Response.Headers.Set(
                    HeaderNameCacheControl,
                    HeaderValueNoCache);

                Response.Headers.Set(
                    HeaderNamePragma,
                    HeaderValueNoCache);

                Response.Headers.Set(
                    HeaderNameExpires,
                    HeaderValueMinusOne);

                bool shouldLoginRedirect = shouldSignin && Options.LoginPath.HasValue && Request.Path == Options.LoginPath;
                bool shouldLogoutRedirect = shouldSignout && Options.LogoutPath.HasValue && Request.Path == Options.LogoutPath;

                if ((shouldLoginRedirect || shouldLogoutRedirect) && Response.StatusCode == 200)
                {
                    IReadableStringCollection query = Request.Query;
                    string redirectUri = query.Get(Options.ReturnUrlParameter);
                    if (!string.IsNullOrWhiteSpace(redirectUri)
                        && IsHostRelative(redirectUri))
                    {
                        var redirectContext = new CookieApplyRedirectContext(Context, Options, redirectUri);
                        Options.Provider.ApplyRedirect(redirectContext);
                    }
                }
            }
            catch (Exception exception)
            {
                CookieExceptionContext exceptionContext = new CookieExceptionContext(Context, Options,
                    CookieExceptionContext.ExceptionLocation.ApplyResponseGrant, exception, model);
                Options.Provider.Exception(exceptionContext);
                if (exceptionContext.Rethrow)
                {
                    throw;
                }
            }
        }

        private static bool IsHostRelative(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }
            if (path.Length == 1)
            {
                return path[0] == '/';
            }
            return path[0] == '/' && path[1] != '/' && path[1] != '\\';
        }

        protected override Task ApplyResponseChallengeAsync()
        {
            if (Response.StatusCode != 401 || !Options.LoginPath.HasValue)
            {
                return Task.FromResult(0);
            }

            AuthenticationResponseChallenge challenge = Helper.LookupChallenge(Options.AuthenticationType, Options.AuthenticationMode);

            try
            {
                if (challenge != null)
                {
                    string loginUri = challenge.Properties.RedirectUri;
                    if (string.IsNullOrWhiteSpace(loginUri))
                    {
                        string currentUri =
                            Request.PathBase +
                            Request.Path +
                            Request.QueryString;

                        loginUri =
                            Request.Scheme +
                            Uri.SchemeDelimiter +
                            Request.Host +
                            Request.PathBase +
                            Options.LoginPath +
                            new QueryString(Options.ReturnUrlParameter, currentUri);
                    }

                    var redirectContext = new CookieApplyRedirectContext(Context, Options, loginUri);
                    Options.Provider.ApplyRedirect(redirectContext);
                }
            }
            catch (Exception exception)
            {
                CookieExceptionContext exceptionContext = new CookieExceptionContext(Context, Options,
                    CookieExceptionContext.ExceptionLocation.ApplyResponseChallenge, exception, ticket: null);
                Options.Provider.Exception(exceptionContext);
                if (exceptionContext.Rethrow)
                {
                    throw;
                }
            }

            return Task.FromResult<object>(null);
        }
    }
}
