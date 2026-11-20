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
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Xml.XPath;
using OpenTween.Api.DataModel;

namespace OpenTween.Api.GraphQL
{
    public class TimelineTweet
    {
        public const string TypeName = nameof(TimelineTweet);

        public XElement Element { get; }

        public bool IsAvailable
            => this.resultElm != null && !this.IsTombstoneResult(this.resultElm) && this.HasLegacyProperty(this.resultElm);

        private readonly XElement? resultElm;

        public TimelineTweet(XElement element)
        {
            var typeName = element.Element("itemType")?.Value;
            if (typeName != TypeName)
                throw new ArgumentException($"Invalid itemType: {typeName}", nameof(element));

            this.Element = element;
            this.resultElm = this.TryGetResultElm();
        }

        private XElement? TryGetResultElm()
            => this.Element.XPathSelectElement("tweet_results/result");

        private bool IsTombstoneResult([NotNullWhen(true)]XElement? resultElm)
            => resultElm?.Element("__typename")?.Value == "TweetTombstone";

        private bool HasLegacyProperty(XElement? resultElm)
            => resultElm?.XPathSelectElement("legacy|tweet/legacy") != null;

        public TwitterStatus ToTwitterStatus()
        {
            this.ThrowIfTweetIsNotAvailable();

            try
            {
                var resultElm = this.resultElm ?? throw CreateParseError();
                var status = TimelineTweet.ParseTweetUnion(resultElm);

                if (this.Element.Element("promotedMetadata") != null)
                    status.IsPromoted = true;

                return status;
            }
            catch (WebApiException ex)
            {
                ex.ResponseText = JsonUtils.JsonXmlToString(this.Element);
                MyCommon.TraceOut(ex);
                throw;
            }
        }

        public void ThrowIfTweetIsNotAvailable()
        {
            if (this.IsAvailable)
                return;

            string? tombstoneText = null;
            if (this.IsTombstoneResult(this.resultElm))
                tombstoneText = this.resultElm.XPathSelectElement("tombstone/text/text")?.Value;

            var message = tombstoneText ?? "Tweet is not available";
            var json = JsonUtils.JsonXmlToString(this.Element);

            throw new WebApiException(message, json);
        }

        public static TwitterStatus ParseTweetUnion(XElement tweetUnionElm)
        {
            var tweetElm = GetTweetTypeName(tweetUnionElm) switch
            {
                "Tweet" => tweetUnionElm,
                "TweetWithVisibilityResults" => tweetUnionElm.Element("tweet") ?? throw CreateParseError(),
                _ => throw CreateParseError(),
            };

            return TimelineTweet.ParseTweet(tweetElm);
        }

        public static string GetTweetTypeName(XElement tweetUnionElm)
            => tweetUnionElm.Element("__typename")?.Value ?? throw CreateParseError();

        private static TwitterStatus? ParseOptionalTweetUnion(XElement? tweetUnionElm)
        {
            if (tweetUnionElm == null)
                return null;

            // X can return unavailable quoted or retweeted posts as a different
            // result type (for example TweetUnavailable). These nested posts are
            // optional; keep the surrounding timeline post instead of failing
            // the entire timeline response.
            var typeName = tweetUnionElm.Element("__typename")?.Value;
            if (typeName != "Tweet" && typeName != "TweetWithVisibilityResults")
                return null;

            return TimelineTweet.ParseTweetUnion(tweetUnionElm);
        }

        public static TwitterStatus ParseTweet(XElement tweetElm)
        {
            var tweetLegacyElm = tweetElm.Element("legacy") ?? throw CreateParseError();
            var userElm = tweetElm.Element("core")?.Element("user_results")?.Element("result") ?? throw CreateParseError();
            var retweetedTweetElm = tweetLegacyElm.Element("retweeted_status_result")?.Element("result");
            var user = new TwitterGraphqlUser(userElm);
            var quotedTweetElm = tweetElm.Element("quoted_status_result")?.Element("result") ?? null;
            var quotedStatusPermalink = tweetLegacyElm.Element("quoted_status_permalink") ?? null;
            var noteTweetResultElm = tweetElm.XPathSelectElement("note_tweet/note_tweet_results/result");
            var noteText = noteTweetResultElm?.Element("text")?.Value;
            var noteEntitiesElm = noteTweetResultElm?.Element("entity_set");
            static string GetText(XElement elm, string name)
                => elm.Element(name)?.Value ?? throw CreateParseError();

            static string? GetTextOrNull(XElement elm, string name)
                => elm.Element(name)?.Value;

            return new()
            {
                IdStr = GetText(tweetElm, "rest_id"),
                Source = GetTextOrNull(tweetElm, "source") ?? "",
                CreatedAt = GetText(tweetLegacyElm, "created_at"),
                // 長文投稿では legacy.full_text が途中で切られているため、Note Tweet の本文を優先する。
                FullText = noteText ?? GetText(tweetLegacyElm, "full_text"),
                InReplyToScreenName = GetTextOrNull(tweetLegacyElm, "in_reply_to_screen_name"),
                InReplyToStatusIdStr = GetTextOrNull(tweetLegacyElm, "in_reply_to_status_id_str"),
                InReplyToUserIdStr = GetTextOrNull(tweetLegacyElm, "in_reply_to_user_id_str"),
                Favorited = GetTextOrNull(tweetLegacyElm, "favorited") is string favorited ? favorited == "true" : null,
                Entities = ParseTextEntities(noteEntitiesElm ?? tweetLegacyElm.Element("entities")),
                ExtendedEntities = new()
                {
                    Media = tweetLegacyElm.XPathSelectElements("extended_entities/media/item")
                        .Select(x => new TwitterEntityMedia()
                        {
                            Indices = x.XPathSelectElements("indices/item").Select(x => int.Parse(x.Value)).ToArray(),
                            DisplayUrl = GetText(x, "display_url"),
                            ExpandedUrl = GetText(x, "expanded_url"),
                            Url = GetText(x, "url"),
                            MediaUrlHttps = GetText(x, "media_url_https"),
                            Type = GetText(x, "type"),
                            AltText = GetTextOrNull(x, "ext_alt_text"),
                        })
                        .ToArray(),
                },
                User = user.ToTwitterUser(),
                RetweetedStatus = ParseOptionalTweetUnion(retweetedTweetElm),
                IsQuoteStatus = GetTextOrNull(tweetLegacyElm, "is_quote_status") == "true",
                QuotedStatus = ParseOptionalTweetUnion(quotedTweetElm),
                Poll = ParsePoll(tweetElm),
                QuotedStatusIdStr = GetTextOrNull(tweetLegacyElm, "quoted_status_id_str"),
                QuotedStatusPermalink = quotedStatusPermalink == null ? null : new()
                {
                    Url = GetText(quotedStatusPermalink, "url"),
                    Expanded = GetText(quotedStatusPermalink, "expanded"),
                    Display = GetText(quotedStatusPermalink, "display"),
                },
            };
        }

        private static TwitterEntities ParseTextEntities(XElement? entitiesElm)
        {
            static string? GetText(XElement elm, string name) => elm.Element(name)?.Value;
            static int[] GetIndices(XElement elm)
                => elm.XPathSelectElements("indices/item").Select(x => int.TryParse(x.Value, out var n) ? n : 0).ToArray();

            return new()
            {
                UserMentions = (entitiesElm?.XPathSelectElements("user_mentions/item") ?? Enumerable.Empty<XElement>())
                    .Select(x => new TwitterEntityMention
                    {
                        Indices = GetIndices(x),
                        IdStr = GetText(x, "id_str") ?? "",
                        ScreenName = GetText(x, "screen_name") ?? "",
                    }).ToArray(),
                Urls = (entitiesElm?.XPathSelectElements("urls/item") ?? Enumerable.Empty<XElement>())
                    .Select(x => new TwitterEntityUrl
                    {
                        Indices = GetIndices(x),
                        DisplayUrl = GetText(x, "display_url"),
                        ExpandedUrl = GetText(x, "expanded_url"),
                        Url = GetText(x, "url") ?? "",
                    }).ToArray(),
                Hashtags = (entitiesElm?.XPathSelectElements("hashtags/item") ?? Enumerable.Empty<XElement>())
                    .Select(x => new TwitterEntityHashtag
                    {
                        Indices = GetIndices(x),
                        Text = GetText(x, "text") ?? "",
                    }).ToArray(),
            };
        }

        private static TwitterPoll? ParsePoll(XElement tweetElm)
        {
            var cardElm = tweetElm.XPathSelectElement("card/legacy");
            if (cardElm == null)
                return null;

            var values = cardElm.XPathSelectElements("binding_values/item")
                .Select(item => new
                {
                    Key = item.Element("key")?.Value ?? item.Element("name")?.Value,
                    Value = item.Element("value")?.Element("string_value")?.Value
                        ?? item.Element("value")?.Element("number_value")?.Value
                        ?? item.Element("value")?.Element("boolean_value")?.Value,
                })
                .Where(x => x.Key != null && x.Value != null)
                .ToDictionary(x => x.Key!, x => x.Value!, StringComparer.OrdinalIgnoreCase);

            var cardName = cardElm.Element("name")?.Value
                ?? (values.TryGetValue("name", out var bindingCardName) ? bindingCardName : null);
            if (cardName == null || !cardName.StartsWith("poll", StringComparison.OrdinalIgnoreCase))
                return null;

            var choices = new List<TwitterPollChoice>();
            for (var i = 1; i <= 4; i++)
            {
                if (!values.TryGetValue($"choice{i}_label", out var label) || string.IsNullOrWhiteSpace(label))
                    continue;

                var votes = values.TryGetValue($"choice{i}_count", out var countText)
                    && int.TryParse(countText, out var count) ? count : 0;
                choices.Add(new(label, votes));
            }

            if (choices.Count == 0)
                return null;

            var totalVotes = values.TryGetValue("total_votes", out var totalText)
                && int.TryParse(totalText, out var total) ? total : choices.Sum(x => x.Votes);
            var endDate = values.TryGetValue("end_datetime_utc", out var endText)
                && DateTimeOffset.TryParse(endText, out var parsedEnd) ? parsedEnd : (DateTimeOffset?)null;
            var isFinal = values.TryGetValue("counts_are_final", out var finalText)
                && bool.TryParse(finalText, out var final) && final;

            return new(
                values.TryGetValue("question", out var question) ? question : null,
                choices,
                totalVotes,
                endDate,
                isFinal
            );
        }

        private static Exception CreateParseError()
            => throw new WebApiException("Parse error on TimelineTweet");

        public static TimelineTweet[] ExtractTimelineTweets(XElement element)
        {
            return element.XPathSelectElements($"//itemContent[itemType[text()='{TypeName}']][tweetDisplayType[text()='Tweet' or text()='SelfThread']]")
                .Select(x => new TimelineTweet(x))
                .ToArray();
        }
    }
}
