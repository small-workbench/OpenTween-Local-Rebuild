// Modified for OpenTween Small Workbench Edition, 2026-11-21. See README.md and MODIFIED-FILES.txt.
// OpenTween - Client of Twitter
// Copyright (c) 2016 kim_upsilon (@kim_upsilon) <https://upsilo.net/~upsilon/>
// All rights reserved.
//
// This file is part of OpenTween.
//
// This program is free software; you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation; either version 3 of the License, or (at your option)
// any later version.
//
// This program is distributed in the hope that it will be useful, but
// WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY
// or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License
// for more details.
//
// You should have received a copy of the GNU General Public License along
// with this program. If not, see <http://www.gnu.org/licenses/>, or write to
// the Free Software Foundation, Inc., 51 Franklin Street - Fifth Floor,
// Boston, MA 02110-1301, USA.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Cache;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using OpenTween.Api;
using OpenTween.Api.DataModel;
using OpenTween.SocialProtocol.Twitter;

namespace OpenTween.Connection
{
    public class TwitterApiConnection : IApiConnection, IDisposable
    {
        public static Uri RestApiBase { get; set; } = new("https://api.twitter.com/1.1/");

        // SettingCommon.xml の TwitterUrl との互換性のために用意
        public static string RestApiHost
        {
            get => RestApiBase.Host;
            set => RestApiBase = new Uri($"https://{value}/1.1/");
        }

        public bool IsDisposed { get; private set; } = false;

        internal HttpClient Http;

        internal ITwitterCredential Credential { get; }

        private readonly TwitterAccountState accountState;

        private readonly Dictionary<string, string> graphqlQueryIds = new(StringComparer.Ordinal);

        private readonly SemaphoreSlim graphqlQueryIdsLock = new(1, 1);

        private readonly SemaphoreSlim transactionIdGeneratorLock = new(1, 1);

        private XClientTransactionIdGenerator? transactionIdGenerator;

        private DateTime transactionIdGeneratorCreatedAt = DateTime.MinValue;

        private static readonly Regex GraphqlBundleUrlRegex = new(
            @"https://abs\.twimg\.com/responsive-web/client-web[\w./-]+\.js",
            RegexOptions.Compiled | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1)
        );

        private static readonly Regex GraphqlQueryIdRegex = new(
            @"queryId:""(?<id>[^""]+)"",operationName:""(?<name>\w+)""",
            RegexOptions.Compiled | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1)
        );

        private static readonly Regex GraphqlOperationNameRegex = new(
            @"operationName:""(?<name>\w+)""(?:(?!operationName:)[\s\S]){0,200}?queryId:""(?<id>[^""]+)""",
            RegexOptions.Compiled | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1)
        );

        public TwitterApiConnection()
            : this(new TwitterCredentialNone(), new())
        {
        }

        public TwitterApiConnection(ITwitterCredential credential, TwitterAccountState accountState)
        {
            this.Credential = credential;
            this.accountState = accountState;

            this.InitializeHttpClients();
            Networking.WebProxyChanged += this.Networking_WebProxyChanged;
        }

        [MemberNotNull(nameof(Http))]
        private void InitializeHttpClients()
        {
            this.Http = InitializeHttpClient(this.Credential);

            // タイムアウト設定は IHttpRequest.Timeout でリクエスト毎に制御する
            this.Http.Timeout = Timeout.InfiniteTimeSpan;
        }

        public void ThrowIfUnauthorizedScope(string scope)
        {
        }

        /// <summary>
        /// X の Web クライアントから GraphQL の操作 ID を取得します。
        /// </summary>
        public async Task<string?> GetCurrentGraphqlQueryIdAsync(string operationName, bool refresh = false)
        {
            if (this.Credential is not TwitterCredentialCookie cookieCredential)
                return null;

            if (!refresh && this.graphqlQueryIds.TryGetValue(operationName, out var cachedQueryId))
                return cachedQueryId;

            await this.graphqlQueryIdsLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!refresh && this.graphqlQueryIds.TryGetValue(operationName, out cachedQueryId))
                    return cachedQueryId;

                var queryIds = await FetchCurrentGraphqlQueryIds(
                    cookieCredential.AppToken.TwitterComCookie,
                    operationName
                )
                    .ConfigureAwait(false);

                foreach (var queryId in queryIds)
                    this.graphqlQueryIds[queryId.Key] = queryId.Value;

                return queryIds.TryGetValue(operationName, out var currentQueryId)
                    ? currentQueryId
                    : null;
            }
            finally
            {
                this.graphqlQueryIdsLock.Release();
            }
        }

        private static async Task<Dictionary<string, string>> FetchCurrentGraphqlQueryIds(
            string rawCookie,
            string requestedOperationName)
        {
            var queryIds = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(rawCookie))
                return queryIds;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var pageClient = CreateXPageHttpClient();

            string? homepage = null;
            var pageUri = new Uri("https://x.com/home");
            try
            {
                for (var redirectCount = 0; redirectCount < 4; redirectCount++)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, pageUri);
                    AddXPageHeaders(request, rawCookie);
                    using var response = await pageClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cts.Token
                    ).ConfigureAwait(false);

                    if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400
                        && response.Headers.Location != null)
                    {
                        var redirectUri = response.Headers.Location.IsAbsoluteUri
                            ? response.Headers.Location
                            : new Uri(pageUri, response.Headers.Location);

                        // 認証情報を X.com 以外へ転送しないよう、同一ホストの遷移だけ追跡します。
                        if (redirectUri.Scheme != Uri.UriSchemeHttps || redirectUri.Host != "x.com")
                            return queryIds;

                        pageUri = redirectUri;
                        continue;
                    }

                    if (!response.IsSuccessStatusCode)
                        return queryIds;

                    homepage = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    break;
                }
            }
            catch (HttpRequestException)
            {
                return queryIds;
            }
            catch (OperationCanceledException)
            {
                return queryIds;
            }

            if (homepage == null)
                return queryIds;

            var pendingUrls = new Queue<string>(ExtractGraphqlBundleUrls(homepage));
            var seenUrls = new HashSet<string>(StringComparer.Ordinal);

            // X のトップページから参照されるスクリプトと、そのスクリプトから参照される分だけを調べます。
            while (pendingUrls.Count > 0 && seenUrls.Count < 40 && !cts.IsCancellationRequested)
            {
                var batch = new List<string>();
                while (pendingUrls.Count > 0 && batch.Count < 6 && seenUrls.Count + batch.Count < 40)
                {
                    var url = pendingUrls.Dequeue();
                    if (seenUrls.Add(url))
                        batch.Add(url);
                }

                if (batch.Count == 0)
                    continue;

                var scripts = await Task.WhenAll(batch.Select(url => FetchPublicBundle(url, cts.Token)))
                    .ConfigureAwait(false);

                foreach (var script in scripts)
                {
                    if (script == null)
                        continue;

                    ExtractGraphqlQueryIds(script, queryIds);
                    if (queryIds.ContainsKey(requestedOperationName))
                        return queryIds;

                    foreach (var url in ExtractGraphqlBundleUrls(script))
                    {
                        if (!seenUrls.Contains(url))
                            pendingUrls.Enqueue(url);
                    }
                }
            }

            return queryIds;
        }

        private static HttpClient CreateXPageHttpClient()
        {
            var builder = Networking.CreateHttpClientBuilder();
            builder.SetupHttpClientHandler(handler => handler.AllowAutoRedirect = false);
            return builder.Build();
        }

        private static void AddXPageHeaders(HttpRequestMessage request, string rawCookie)
        {
            request.Headers.UserAgent.ParseAdd(XClientTransactionIdGenerator.BrowserUserAgent);
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            request.Headers.AcceptLanguage.ParseAdd("ja,en;q=0.8");
            request.Headers.TryAddWithoutValidation("cookie", CreateXPageCookieValue(rawCookie));
        }

        private static string CreateXPageCookieValue(string rawCookie)
        {
            var wantedKeys = new HashSet<string>(new[] { "auth_token", "ct0", "twid", "lang" }, StringComparer.Ordinal);
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in rawCookie.Split(';')
                .Select(x => x.Trim().Split(new[] { '=' }, 2)))
            {
                if (pair.Length == 2 && wantedKeys.Contains(pair[0]))
                    values[pair[0]] = pair[1];
            }

            return string.Join("; ", new[] { "auth_token", "ct0", "twid", "lang" }
                .Where(values.ContainsKey)
                .Select(key => $"{key}={values[key]}"));
        }

        private async Task<string?> GetCurrentClientTransactionIdAsync(string method, Uri requestUri)
        {
            if (this.Credential is not TwitterCredentialCookie cookieCredential)
                return null;

            if (this.transactionIdGenerator != null
                && DateTime.UtcNow - this.transactionIdGeneratorCreatedAt < TimeSpan.FromMinutes(15))
                return this.transactionIdGenerator.Generate(method, requestUri.AbsolutePath);

            await this.transactionIdGeneratorLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (this.transactionIdGenerator != null
                    && DateTime.UtcNow - this.transactionIdGeneratorCreatedAt < TimeSpan.FromMinutes(15))
                    return this.transactionIdGenerator.Generate(method, requestUri.AbsolutePath);

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var pageClient = CreateXPageHttpClient();

                var pageUris = new[]
                {
                    new Uri("https://x.com/i/jf/"),
                    new Uri("https://x.com/home"),
                    new Uri("https://x.com/"),
                };

                foreach (var pageUri in pageUris)
                {
                    var homeHtml = await FetchXPageHtml(
                        pageClient,
                        cookieCredential.AppToken.TwitterComCookie,
                        pageUri,
                        cts.Token
                    ).ConfigureAwait(false);

                    if (homeHtml == null)
                        continue;

                    var bundleUrl = XClientTransactionIdGenerator.FindOnDemandFileUrl(homeHtml);
                    if (bundleUrl == null)
                        continue;

                    var bundle = await FetchPublicBundle(bundleUrl, cts.Token).ConfigureAwait(false);
                    if (bundle == null)
                        continue;

                    try
                    {
                        this.transactionIdGenerator = XClientTransactionIdGenerator.Create(homeHtml, bundle);
                        this.transactionIdGeneratorCreatedAt = DateTime.UtcNow;
                        return this.transactionIdGenerator.Generate(method, requestUri.AbsolutePath);
                    }
                    catch (FormatException)
                    {
                        // Try the next web-app shell; X can serve different shells on these routes.
                    }
                }

                return null;
            }
            finally
            {
                this.transactionIdGeneratorLock.Release();
            }
        }

        private static async Task<string?> FetchXPageHtml(
            HttpClient pageClient,
            string rawCookie,
            Uri startingUri,
            CancellationToken cancellationToken)
        {
            var pageUri = startingUri;
            for (var redirectCount = 0; redirectCount < 4; redirectCount++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, pageUri);
                AddXPageHeaders(request, rawCookie);
                using var response = await pageClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken
                ).ConfigureAwait(false);

                if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400
                    && response.Headers.Location != null)
                {
                    var redirectUri = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location
                        : new Uri(pageUri, response.Headers.Location);

                    if (redirectUri.Scheme != Uri.UriSchemeHttps || redirectUri.Host != "x.com")
                        return null;

                    pageUri = redirectUri;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    return null;

                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }

            return null;
        }

        private static async Task<string?> FetchPublicBundle(string url, CancellationToken cancellationToken)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd(XClientTransactionIdGenerator.BrowserUserAgent);
                using var response = await Networking.Http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken
                ).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                    return null;

                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                return null;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        private static IEnumerable<string> ExtractGraphqlBundleUrls(string text)
            => GraphqlBundleUrlRegex.Matches(text)
                .Cast<Match>()
                .Select(x => x.Value)
                .Distinct(StringComparer.Ordinal);

        private static void ExtractGraphqlQueryIds(string text, Dictionary<string, string> queryIds)
        {
            foreach (Match match in GraphqlQueryIdRegex.Matches(text))
                queryIds[match.Groups["name"].Value] = match.Groups["id"].Value;

            foreach (Match match in GraphqlOperationNameRegex.Matches(text))
            {
                var operationName = match.Groups["name"].Value;
                if (!queryIds.ContainsKey(operationName))
                    queryIds[operationName] = match.Groups["id"].Value;
            }
        }

        public async Task<ApiResponse> SendAsync(IHttpRequest request)
        {
            var endpointName = request.EndpointName;

            // レートリミット規制中はAPIリクエストを送信せずに直ちにエラーを発生させる
            if (endpointName != null)
                this.ThrowIfRateLimitExceeded(endpointName);

            using var requestMessage = request.CreateMessage(RestApiBase);

            if (this.Credential is TwitterCredentialCookie
                && requestMessage.RequestUri is Uri requestUri
                && requestUri.Host == "x.com"
                && requestUri.AbsolutePath.StartsWith("/i/api/graphql/", StringComparison.Ordinal)
                && (requestUri.AbsolutePath.EndsWith("/CreateTweet", StringComparison.Ordinal)
                    || requestUri.AbsolutePath.EndsWith("/SearchTimeline", StringComparison.Ordinal)
                    || requestUri.AbsolutePath.EndsWith("/CreateRetweet", StringComparison.Ordinal)
                    || requestUri.AbsolutePath.EndsWith("/DeleteRetweet", StringComparison.Ordinal)
                    || requestUri.AbsolutePath.EndsWith("/UserByScreenName", StringComparison.Ordinal)
                    || requestUri.AbsolutePath.EndsWith("/UserTweetsAndReplies", StringComparison.Ordinal)))
            {
                var transactionId = await this.GetCurrentClientTransactionIdAsync(
                    requestMessage.Method.Method,
                    requestUri
                ).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(transactionId))
                {
                    throw new WebApiException(
                        "XのWebクライアント署名を取得できなかったため、投稿・検索のリクエストは送信しませんでした。"
                    );
                }

                requestMessage.Headers.TryAddWithoutValidation("x-client-transaction-id", transactionId);
                requestMessage.Headers.TryAddWithoutValidation("x-twitter-active-user", "yes");
                requestMessage.Headers.TryAddWithoutValidation("x-twitter-client-language", "ja");
                requestMessage.Headers.TryAddWithoutValidation("Origin", "https://x.com");
                requestMessage.Headers.Referrer = new Uri("https://x.com/home");
                requestMessage.Headers.Accept.ParseAdd("*/*");
                requestMessage.Headers.UserAgent.Clear();
                requestMessage.Headers.UserAgent.ParseAdd(XClientTransactionIdGenerator.BrowserUserAgent);
            }

            HttpResponseMessage? responseMessage = null;
            try
            {
                responseMessage = await HandleTimeout(
                    (token) => this.Http.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, token),
                    request.Timeout
                );

                if (endpointName != null)
                    this.accountState.RateLimits.UpdateFromHeader(responseMessage.Headers, endpointName);

                await TwitterApiConnection.CheckStatusCode(responseMessage, this.accountState)
                    .ConfigureAwait(false);

                var response = new ApiResponse(responseMessage);
                responseMessage = null; // responseMessage は ApiResponse で使用するため破棄されないようにする

                return response;
            }
            catch (HttpRequestException ex)
            {
                throw TwitterApiException.CreateFromException(ex);
            }
            catch (OperationCanceledException ex)
            {
                throw TwitterApiException.CreateFromException(ex);
            }
            finally
            {
                responseMessage?.Dispose();
            }
        }

        /// <summary>
        /// 指定されたエンドポイントがレートリミット規制中であれば例外を発生させる
        /// </summary>
        private void ThrowIfRateLimitExceeded(string endpointName)
        {
            var limit = this.accountState.RateLimits[endpointName];
            if (limit == null)
                return;

            if (limit.AccessLimitRemain == 0 && limit.AccessLimitResetDate > DateTimeUtc.Now)
            {
                var error = new TwitterError
                {
                    Errors = new[]
                    {
                        new TwitterErrorItem { Code = TwitterErrorCode.RateLimit, Message = "" },
                    },
                };
                throw new TwitterApiException(0, error, "");
            }
        }

        public static async Task<T> HandleTimeout<T>(Func<CancellationToken, Task<T>> func, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource();
            var cancellactionToken = cts.Token;

            var task = Task.Run(() => func(cancellactionToken), cancellactionToken);
            var timeoutTask = Task.Delay(timeout);

            if (await Task.WhenAny(task, timeoutTask) == timeoutTask)
            {
                // タイムアウト

                // キャンセル後のタスクで発生した例外は無視する
                _ = AsyncExceptionBoundary.IgnoreExceptionAndDispose(task);
                cts.Cancel();

                throw new OperationCanceledException("Timeout", cancellactionToken);
            }

            return await task;
        }

        protected static async Task CheckStatusCode(HttpResponseMessage response, TwitterAccountState? accountState)
        {
            var statusCode = response.StatusCode;

            if ((int)statusCode >= 200 && (int)statusCode <= 299)
            {
                if (accountState != null)
                    accountState.HasUnrecoverableError = false;
                return;
            }

            string responseText;
            using (var content = response.Content)
            {
                responseText = await content.ReadAsStringAsync()
                    .ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(responseText))
            {
                if (statusCode == HttpStatusCode.Unauthorized)
                {
                    if (accountState != null)
                        accountState.HasUnrecoverableError = true;
                }

                throw new TwitterApiException(statusCode, responseText);
            }

            try
            {
                var error = TwitterError.ParseJson(responseText);

                if (error?.Errors == null || error.Errors.Length == 0)
                    throw new TwitterApiException(statusCode, responseText);

                var errorCodes = error.Errors.Select(x => x.Code);
                if (errorCodes.Any(x => x == TwitterErrorCode.InternalError || x == TwitterErrorCode.SuspendedAccount))
                {
                    if (accountState != null)
                        accountState.HasUnrecoverableError = true;
                }

                throw new TwitterApiException(statusCode, error, responseText);
            }
            catch (SerializationException)
            {
                throw new TwitterApiException(statusCode, responseText);
            }
        }

        public OAuthEchoHandler CreateOAuthEchoHandler(HttpMessageHandler innerHandler, Uri authServiceProvider, Uri? realm = null)
        {
            var uri = new Uri(RestApiBase, authServiceProvider);

            if (this.Credential is TwitterCredentialOAuth1 oauthCredential)
            {
                return OAuthEchoHandler.CreateHandler(
                    innerHandler,
                    uri,
                    oauthCredential.AppToken.OAuth1ConsumerKey,
                    oauthCredential.AppToken.OAuth1ConsumerSecret,
                    oauthCredential.Token,
                    oauthCredential.TokenSecret,
                    realm);
            }
            else
            {
                // MobipictureApi クラス向けの暫定対応
                return OAuthEchoHandler.CreateHandler(
                    innerHandler,
                    uri,
                    ApiKey.Create(""),
                    ApiKey.Create(""),
                    "",
                    "",
                    realm);
            }
        }

        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (this.IsDisposed)
                return;

            this.IsDisposed = true;

            if (disposing)
            {
                Networking.WebProxyChanged -= this.Networking_WebProxyChanged;
                this.Http.Dispose();
                this.transactionIdGeneratorLock.Dispose();
            }
        }

        ~TwitterApiConnection()
            => this.Dispose(false);

        private void Networking_WebProxyChanged(object sender, EventArgs e)
            => this.InitializeHttpClients();

        public static async Task<TwitterCredentialOAuth1> GetRequestTokenAsync(TwitterAppToken appToken)
        {
            var emptyCredential = new TwitterCredentialOAuth1(appToken, "", "");
            var param = new Dictionary<string, string>
            {
                ["oauth_callback"] = "oob",
            };
            var response = await GetOAuthTokenAsync(new Uri("https://api.twitter.com/oauth/request_token"), param, emptyCredential)
                .ConfigureAwait(false);

            return new(appToken, response["oauth_token"], response["oauth_token_secret"]);
        }

        public static Uri GetAuthorizeUri(TwitterCredentialOAuth1 requestToken, string? screenName = null)
        {
            var param = new Dictionary<string, string>
            {
                ["oauth_token"] = requestToken.Token,
            };

            if (screenName != null)
                param["screen_name"] = screenName;

            return new Uri("https://api.twitter.com/oauth/authorize?" + MyCommon.BuildQueryString(param));
        }

        public static async Task<IDictionary<string, string>> GetAccessTokenAsync(TwitterCredentialOAuth1 credential, string verifier)
        {
            var param = new Dictionary<string, string>
            {
                ["oauth_verifier"] = verifier,
            };
            var response = await GetOAuthTokenAsync(new Uri("https://api.twitter.com/oauth/access_token"), param, credential)
                .ConfigureAwait(false);

            return response;
        }

        private static async Task<IDictionary<string, string>> GetOAuthTokenAsync(
            Uri uri,
            IDictionary<string, string> param,
            TwitterCredentialOAuth1 credential
        )
        {
            using var authorizeClient = InitializeHttpClient(credential);

            var requestUri = new Uri(uri, "?" + MyCommon.BuildQueryString(param));

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
                using var response = await authorizeClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
                    .ConfigureAwait(false);

                using var content = response.Content;
                var responseText = await content.ReadAsStringAsync()
                    .ConfigureAwait(false);

                await TwitterApiConnection.CheckStatusCode(response, accountState: null)
                    .ConfigureAwait(false);

                var responseParams = HttpUtility.ParseQueryString(responseText);

                return responseParams.Cast<string>()
                    .ToDictionary(x => x, x => responseParams[x]);
            }
            catch (HttpRequestException ex)
            {
                throw TwitterApiException.CreateFromException(ex);
            }
            catch (OperationCanceledException ex)
            {
                throw TwitterApiException.CreateFromException(ex);
            }
        }

        private static HttpClient InitializeHttpClient(ITwitterCredential credential)
        {
            var builder = Networking.CreateHttpClientBuilder();

            builder.SetupHttpClientHandler(
                x =>
                {
                    x.CachePolicy = new RequestCachePolicy(RequestCacheLevel.BypassCache);
                    if (credential is TwitterCredentialCookie)
                        x.AllowAutoRedirect = false;
                }
            );

            builder.AddHandler(x => credential.CreateHttpHandler(x));

            return builder.Build();
        }
    }
}
