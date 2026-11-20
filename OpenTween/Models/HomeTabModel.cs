// Modified for OpenTween Small Workbench Edition, 2026-11-21. See README.md and MODIFIED-FILES.txt.
// OpenTween - Client of Twitter
// Copyright (c) 2007-2011 kiri_feather (@kiri_feather) <kiri.feather@gmail.com>
//           (c) 2008-2011 Moz (@syo68k)
//           (c) 2008-2011 takeshik (@takeshik) <http://www.takeshik.org/>
//           (c) 2010-2011 anis774 (@anis774) <http://d.hatena.ne.jp/anis774/>
//           (c) 2010-2011 fantasticswallow (@f_swallow) <http://twitter.com/f_swallow>
//           (c) 2011      Egtra (@egtra) <http://dev.activebasic.com/egtra/>
//           (c) 2012      kim_upsilon (@kim_upsilon) <https://upsilo.net/~upsilon/>
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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenTween.Setting;
using OpenTween.SocialProtocol;
using OpenTween.SocialProtocol.Twitter;

namespace OpenTween.Models
{
    public class HomeTabModel : TabModel
    {
        public override MyCommon.TabUsageType TabType
            => MyCommon.TabUsageType.Home;

        public int TweetsPerHour => this.tweetsPerHour;

        // 流速計測用
        private int tweetsPerHour = 0;
        private readonly ConcurrentDictionary<DateTimeUtc, int> tweetsTimestamps = new();

        private HashSet<PostId> previousHomePage = new();

        public HomeTabModel()
            : this(MyCommon.DEFAULTTAB.RECENT)
        {
        }

        public HomeTabModel(string tabName)
            : base(tabName)
        {
        }

        public override void AddPostQueue(PostClass post)
        {
            base.AddPostQueue(post);
            this.UpdateTimelineSpeed(post.CreatedAt);
        }

        public override async Task RefreshAsync(ISocialAccount account, bool backward, IProgress<string> progress)
        {
            progress.Report(string.Format(Properties.Resources.GetTimelineWorker_RunWorkerCompletedText5, backward ? -1 : 1));

            var firstLoad = !this.IsFirstLoadCompleted;
            var count = Twitter.GetApiResultCount(MyCommon.WORKERTYPE.Timeline, backward, firstLoad);
            var cursor = backward ? this.CursorBottom : this.CursorTop;

            var response = await account.Client.GetHomeTimeline(count, cursor, firstLoad)
                .ConfigureAwait(false);

            var posts = response.Posts.ToList();
            var reachedPreviousPage = this.previousHomePage.Count == 0
                || posts.Any(post => this.previousHomePage.Contains(post.StatusId));
            if (!backward && account.Client is TwitterGraphqlClient && !reachedPreviousPage && posts.Count > 0)
            {
                var next = response.CursorBottom;
                for (var page = 0; page < 3 && next != null; page++)
                {
                    var older = await account.Client.GetHomeTimeline(count, next, firstLoad)
                        .ConfigureAwait(false);
                    posts.AddRange(older.Posts);
                    reachedPreviousPage = older.Posts.Any(post => this.previousHomePage.Contains(post.StatusId));
                    if (reachedPreviousPage || Equals(next, older.CursorBottom))
                        break;

                    next = older.CursorBottom;
                }
            }

            if (!backward && response.Posts.Any())
                this.previousHomePage = new(response.Posts.Select(post => post.StatusId));

            var addedIds = new HashSet<PostId>();
            foreach (var post in posts.Where(post => addedIds.Add(post.StatusId)))
                TabInformations.GetInstance().AddPost(post);

            // 新着時未読クリア
            if (SettingManager.Instance.Common.ReadOldPosts)
                TabInformations.GetInstance().SetReadHomeTab();

            TabInformations.GetInstance().DistributePosts();

            if (response.CursorTop != null && !backward)
                this.CursorTop = response.CursorTop;

            if (response.CursorBottom != null && (backward || this.CursorBottom == null))
                this.CursorBottom = response.CursorBottom;

            if (firstLoad)
                this.IsFirstLoadCompleted = true;

            progress.Report(Properties.Resources.GetTimelineWorker_RunWorkerCompletedText1);
        }

        /// <summary>
        /// タイムラインに追加された発言件数を反映し、タイムラインの流速を更新します
        /// </summary>
        private void UpdateTimelineSpeed(DateTimeUtc postCreatedAt)
        {
            var now = DateTimeUtc.Now;

            // 1 時間以上前の時刻は追加しない
            var oneHour = TimeSpan.FromHours(1);
            if (now - postCreatedAt > oneHour)
                return;

            this.tweetsTimestamps.AddOrUpdate(postCreatedAt, 1, (k, v) => v + 1);

            var removeKeys = new List<DateTimeUtc>();
            var tweetsInWindow = 0;
            foreach (var (timestamp, count) in this.tweetsTimestamps)
            {
                if (now - timestamp > oneHour)
                    removeKeys.Add(timestamp);
                else
                    tweetsInWindow += count;
            }
            Interlocked.Exchange(ref this.tweetsPerHour, tweetsInWindow);

            foreach (var key in removeKeys)
                this.tweetsTimestamps.TryRemove(key, out _);
        }
    }
}
