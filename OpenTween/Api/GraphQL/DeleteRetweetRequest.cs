// Modified for OpenTween Small Workbench Edition, 2026-11-21. See README.md and MODIFIED-FILES.txt.
// OpenTween - Client of Twitter
// Copyright (c) 2023 kim_upsilon (@kim_upsilon) <https://upsilo.net/~upsilon/>
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
using System.Threading.Tasks;
using OpenTween.Connection;
using OpenTween.Models;

namespace OpenTween.Api.GraphQL
{
    public class DeleteRetweetRequest
    {
        private static readonly Uri EndpointUri = new("https://x.com/i/api/graphql/ZyZigVsNiFO6v1dEks1eWg/DeleteRetweet");

        public required TwitterStatusId SourceTweetId { get; set; }

        public string CreateRequestBody()
        {
            return $$"""
            {"variables":{"source_tweet_id":"{{JsonUtils.EscapeJsonString(this.SourceTweetId.Id)}}","dark_request":false},"queryId":"ZyZigVsNiFO6v1dEks1eWg"}
            """;
        }

        public async Task Send(IApiConnection apiConnection)
        {
            var request = new PostJsonRequest
            {
                RequestUri = EndpointUri,
                JsonString = this.CreateRequestBody(),
            };

            using var response = await apiConnection.SendAsync(request)
                .ConfigureAwait(false);

            var rootElm = await response.ReadAsJsonXml()
                .ConfigureAwait(false);

            ErrorResponse.ThrowIfError(rootElm);
        }
    }
}
