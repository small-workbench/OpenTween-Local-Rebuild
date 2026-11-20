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
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Xml.XPath;
using OpenTween.Api.DataModel;

namespace OpenTween.Api.GraphQL
{
    public class TwitterGraphqlUser
    {
        public const string TypeName = "User";

        public XElement Element { get; }

        public TwitterGraphqlUser(XElement element)
        {
            var typeName = element.Element("__typename")?.Value;
            if (typeName != TypeName)
                throw new ArgumentException($"Invalid itemType: {typeName}", nameof(element));

            this.Element = element;
        }

        public TwitterUser ToTwitterUser()
        {
            try
            {
                return TwitterGraphqlUser.ParseUser(this.Element);
            }
            catch (WebApiException ex)
            {
                ex.ResponseText = JsonUtils.JsonXmlToString(this.Element);
                MyCommon.TraceOut(ex);
                throw;
            }
        }

        public static TwitterUser ParseUser(XElement userElm)
        {
            var userLegacyElm = userElm.Element("legacy");
            if (userLegacyElm == null)
            {
                var core = userElm.Element("core") ?? throw CreateParseError();
                userLegacyElm = new XElement("legacy",
                    new XElement("name", core.Element("name")?.Value ?? throw CreateParseError()),
                    new XElement("screen_name", core.Element("screen_name")?.Value ?? throw CreateParseError()),
                    new XElement("created_at", core.Element("created_at")?.Value ?? throw CreateParseError()),
                    new XElement("profile_image_url_https", userElm.XPathSelectElement("avatar/image_url")?.Value ?? ""),
                    new XElement("protected", userElm.XPathSelectElement("privacy/protected")?.Value ?? "false"),
                    new XElement("verified", userElm.XPathSelectElement("verification/verified")?.Value ?? "false"),
                    new XElement("followers_count", userElm.XPathSelectElement("relationship_counts/followers")?.Value ?? "0"),
                    new XElement("friends_count", userElm.XPathSelectElement("relationship_counts/following")?.Value ?? "0"),
                    new XElement("favourites_count", userElm.XPathSelectElement("action_counts/favorites_count")?.Value ?? "0"),
                    new XElement("statuses_count", userElm.XPathSelectElement("tweet_counts/tweets")?.Value ?? "0"),
                    new XElement("description", userElm.XPathSelectElement("profile_bio/description")?.Value ?? ""),
                    new XElement("location", userElm.XPathSelectElement("location/location")?.Value ?? ""),
                    new XElement("url", userElm.XPathSelectElement("website/url")?.Value ?? ""),
                    new XElement("entities",
                        new XElement("description", userElm.XPathSelectElement("profile_bio/entities/description")?.Elements()),
                        new XElement("url", userElm.XPathSelectElement("website/entities/url")?.Elements())));
            }

            static string GetText(XElement elm, string name)
                => elm.Element(name)?.Value ?? throw CreateParseError();

            static string? GetTextOrNull(XElement elm, string name)
                => elm.Element(name)?.Value;

            return new()
            {
                IdStr = GetText(userElm, "rest_id"),
                Name = GetText(userLegacyElm, "name"),
                ProfileImageUrlHttps = GetText(userLegacyElm, "profile_image_url_https"),
                ScreenName = GetText(userLegacyElm, "screen_name"),
                Protected = GetTextOrNull(userLegacyElm, "protected") == "true",
                Verified = GetTextOrNull(userLegacyElm, "verified") == "true",
                CreatedAt = GetText(userLegacyElm, "created_at"),
                FollowersCount = int.Parse(GetText(userLegacyElm, "followers_count")),
                FriendsCount = int.Parse(GetText(userLegacyElm, "friends_count")),
                FavouritesCount = int.Parse(GetText(userLegacyElm, "favourites_count")),
                StatusesCount = int.Parse(GetText(userLegacyElm, "statuses_count")),
                Description = GetTextOrNull(userLegacyElm, "description"),
                Location = GetTextOrNull(userLegacyElm, "location"),
                Url = GetTextOrNull(userLegacyElm, "url"),
                Entities = new()
                {
                    Description = new()
                    {
                        Urls = userLegacyElm.XPathSelectElements("entities/description/urls/item")
                            .Select(x => new TwitterEntityUrl()
                            {
                                Indices = x.XPathSelectElements("indices/item").Select(x => int.Parse(x.Value)).ToArray(),
                                DisplayUrl = GetTextOrNull(x, "display_url"),
                                ExpandedUrl = GetTextOrNull(x, "expanded_url"),
                                Url = GetText(x, "url"),
                            })
                            .ToArray(),
                    },
                    Url = new()
                    {
                        Urls = userLegacyElm.XPathSelectElements("entities/url/urls/item")
                            .Select(x => new TwitterEntityUrl()
                            {
                                Indices = x.XPathSelectElements("indices/item").Select(x => int.Parse(x.Value)).ToArray(),
                                DisplayUrl = GetTextOrNull(x, "display_url"),
                                ExpandedUrl = GetTextOrNull(x, "expanded_url"),
                                Url = GetText(x, "url"),
                            })
                            .ToArray(),
                    },
                },
            };
        }

        private static Exception CreateParseError()
            => throw new WebApiException("Parse error on User");
    }
}
