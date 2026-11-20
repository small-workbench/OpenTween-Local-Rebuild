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

using System.Linq;
using OpenTween.Api.DataModel;
using OpenTween.Models;

namespace OpenTween.Api.GraphQL
{
    public record TimelineGraphqlResponse(
        TimelineTweet[] Tweets,
        QueryCursor<TwitterGraphqlCursor>? CursorTop,
        QueryCursor<TwitterGraphqlCursor>? CursorBottom
    )
    {
        public TwitterStatus[] ToTwitterStatuses()
        {
            var statuses = new System.Collections.Generic.List<TwitterStatus>();
            foreach (var tweet in this.Tweets.Where(x => x.IsAvailable))
            {
                try
                {
                    statuses.Add(tweet.ToTwitterStatus());
                }
                catch (System.Exception ex) when (ex is WebApiException
                    || ex is System.FormatException || ex is System.OverflowException
                    || ex is System.ArgumentException || ex is System.InvalidOperationException)
                {
                    // Keep a malformed entry from discarding the other posts in this page.
                    MyCommon.TraceOut(ex, "Timeline entry could not be parsed.");
                }
            }
            return statuses.ToArray();
        }
    }
}