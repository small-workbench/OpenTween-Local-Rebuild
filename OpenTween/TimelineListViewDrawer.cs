// Modified for OpenTween Small Workbench Edition, 2026-11-21. See README.md and MODIFIED-FILES.txt.
// OpenTween - Client of Twitter
// Copyright (c) 2007-2011 kiri_feather (@kiri_feather) <kiri.feather@gmail.com>
//           (c) 2008-2011 Moz (@syo68k)
//           (c) 2008-2011 takeshik (@takeshik) <http://www.takeshik.org/>
//           (c) 2010-2011 anis774 (@anis774) <http://d.hatena.ne.jp/anis774/>
//           (c) 2010-2011 fantasticswallow (@f_swallow) <http://twitter.com/f_swallow>
//           (c) 2011      kim_upsilon (@kim_upsilon) <https://upsilo.net/~upsilon/>
// All rights reserved.
//
// This file is part of OpenTween.
//
// This program is free software; you can redistribute it and/or modify it
// under the terms of the GNU General public License as published by the Free
// Software Foundation; either version 3 of the License, or (at your option)
// any later version.
//
// This program is distributed in the hope that it will be useful, but
// WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY
// or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General public License
// for more details.
//
// You should have received a copy of the GNU General public License along
// with this program. If not, see <http://www.gnu.org/licenses/>, or write to
// the Free Software Foundation, Inc., 51 Franklin Street - Fifth Floor,
// Boston, MA 02110-1301, USA.

#nullable enable

using System;
using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;
using OpenTween.Models;
using OpenTween.OpenTweenCustomControl;

namespace OpenTween
{
    public sealed class TimelineListViewDrawer : IDisposable
    {
        public bool IsDisposed { get; private set; } = false;

        public ThemeManager Theme { get; set; }

        public MyCommon.IconSizes IconSize { get; set; }

        private bool Use2ColumnsMode
            => this.IconSize == MyCommon.IconSizes.Icon48_2;

        private int IconSizeNumeric
            => this.IconSize switch
            {
                MyCommon.IconSizes.Icon16 => 16,
                MyCommon.IconSizes.Icon24 => 26, // 24x24の場合に26と指定しているのはMSゴシック系フォントのための仕様
                MyCommon.IconSizes.Icon48 => 48,
                MyCommon.IconSizes.Icon48_2 => 48,
                _ => 0,
            };

        private readonly DetailsListView listView;
        private readonly OTBaseForm parentForm;
        private readonly TabModel tab;
        private readonly TimelineListViewCache listViewCache;
        private readonly ImageCache iconCache;
        private readonly ImageList listViewImageList = new(); // ListViewItemの高さ変更用
        private long drawItemCount;
        private long drawItemTotalTicks;
        private long drawItemMaxTicks;
        private long drawSubItemCount;
        private long drawSubItemTotalTicks;
        private long drawSubItemMaxTicks;
        private long postTextDrawCount;
        private long postTextDrawTotalTicks;
        private long postTextDrawMaxTicks;
        private long otherTextDrawCount;
        private long otherTextDrawTotalTicks;
        private long otherTextDrawMaxTicks;

        public TimelineListViewDrawer(
            DetailsListView listView,
            TabModel tab,
            TimelineListViewCache listViewCache,
            ImageCache iconCache,
            ThemeManager theme
        )
        {
            this.listView = listView;
            this.parentForm = (OTBaseForm)listView.FindForm();
            this.tab = tab;
            this.listViewCache = listViewCache;
            this.iconCache = iconCache;
            this.Theme = theme;

            this.RegisterHandlers();
            this.listView.SmallImageList = this.listViewImageList;
            this.listView.OwnerDraw = true;
        }

        private void RegisterHandlers()
        {
            this.listView.DrawItem += this.ListView_DrawItem;
            this.listView.DrawSubItem += this.ListView_DrawSubItem;
        }

        private void UnregisterHandlers()
        {
            this.listView.DrawItem -= this.ListView_DrawItem;
            this.listView.DrawSubItem -= this.ListView_DrawSubItem;
        }

        public void UpdateItemHeight()
        {
            // ディスプレイの DPI 設定を考慮したサイズを設定する
            var scaledIconHeight = this.IconSize != MyCommon.IconSizes.IconNone
                ? this.listView.LogicalToDeviceUnits(this.IconSizeNumeric)
                : 1;

            // アイコンサイズと発言一覧のフォントサイズのどちらか大きい方を一件分の高さとする
            var fontHeight = this.Theme.FontReaded.Height;
            var itemHeight = Math.Max(scaledIconHeight, fontHeight);

            this.listViewImageList.ImageSize = new(1, itemHeight);
        }

        public void ResetPerformanceMetrics()
        {
            this.drawItemCount = 0;
            this.drawItemTotalTicks = 0;
            this.drawItemMaxTicks = 0;
            this.drawSubItemCount = 0;
            this.drawSubItemTotalTicks = 0;
            this.drawSubItemMaxTicks = 0;
            this.postTextDrawCount = 0;
            this.postTextDrawTotalTicks = 0;
            this.postTextDrawMaxTicks = 0;
            this.otherTextDrawCount = 0;
            this.otherTextDrawTotalTicks = 0;
            this.otherTextDrawMaxTicks = 0;
            this.listViewCache.ResetPerformanceMetrics();
            this.listView.ResetRenderMessageMetrics();
        }

        public string TakePerformanceMetrics()
        {
            var cache = this.listViewCache.TakePerformanceMetrics();
            var messageMetrics = this.listView.TakeRenderMessageMetrics();
            var result = string.Join(Environment.NewLine, new[]
            {
                FormatPerformanceMetric("行描画（背景・アイコン）", this.drawItemCount, this.drawItemTotalTicks, this.drawItemMaxTicks),
                FormatPerformanceMetric("セル描画", this.drawSubItemCount, this.drawSubItemTotalTicks, this.drawSubItemMaxTicks),
                FormatPerformanceMetric(
                    "文字描画（本文＋他列）",
                    this.postTextDrawCount + this.otherTextDrawCount,
                    this.postTextDrawTotalTicks + this.otherTextDrawTotalTicks,
                    Math.Max(this.postTextDrawMaxTicks, this.otherTextDrawMaxTicks)),
                FormatPerformanceMetric("投稿本文の文字描画", this.postTextDrawCount, this.postTextDrawTotalTicks, this.postTextDrawMaxTicks),
                FormatPerformanceMetric("他列の文字描画", this.otherTextDrawCount, this.otherTextDrawTotalTicks, this.otherTextDrawMaxTicks),
                FormatPerformanceMetric("行キャッシュ作成", cache.Count, cache.TotalTicks, cache.MaxTicks),
                messageMetrics,
            });

            this.ResetPerformanceMetrics();
            return result;
        }

        private static string FormatPerformanceMetric(string label, long count, long totalTicks, long maxTicks)
        {
            var totalMilliseconds = totalTicks * 1000d / Stopwatch.Frequency;
            var averageMilliseconds = count == 0 ? 0 : totalMilliseconds / count;
            var maxMilliseconds = maxTicks * 1000d / Stopwatch.Frequency;
            return $"{label}: {count}回、平均 {averageMilliseconds:F3} ms、最大 {maxMilliseconds:F3} ms、合計 {totalMilliseconds:F1} ms";
        }

        private static void RecordPerformanceMetric(long startTicks, ref long count, ref long totalTicks, ref long maxTicks)
        {
            var elapsedTicks = Stopwatch.GetTimestamp() - startTicks;
            AccumulatePerformanceMetric(elapsedTicks, ref count, ref totalTicks, ref maxTicks);
        }

        private void RecordTextPerformanceMetric(long startTicks, bool isPostText)
        {
            var elapsedTicks = Stopwatch.GetTimestamp() - startTicks;

            if (isPostText)
                AccumulatePerformanceMetric(elapsedTicks, ref this.postTextDrawCount, ref this.postTextDrawTotalTicks, ref this.postTextDrawMaxTicks);
            else
                AccumulatePerformanceMetric(elapsedTicks, ref this.otherTextDrawCount, ref this.otherTextDrawTotalTicks, ref this.otherTextDrawMaxTicks);
        }

        private static void AccumulatePerformanceMetric(long elapsedTicks, ref long count, ref long totalTicks, ref long maxTicks)
        {
            count++;
            totalTicks += elapsedTicks;
            maxTicks = Math.Max(maxTicks, elapsedTicks);
        }

        private void DrawListViewItemIcon(DrawListViewItemEventArgs e)
        {
            if (this.IconSize == 0) return;

            var item = e.Item;

            // e.Bounds.Leftが常に0を指すから自前で計算
            var itemRect = item.Bounds;
            var col0 = e.Item.ListView.Columns[0];
            itemRect.Width = col0.Width;

            if (col0.DisplayIndex > 0)
            {
                foreach (ColumnHeader clm in e.Item.ListView.Columns)
                {
                    if (clm.DisplayIndex < col0.DisplayIndex)
                        itemRect.X += clm.Width;
                }
            }

            // ディスプレイの DPI 設定を考慮したアイコンサイズ
            var scaleFactor = this.listView.DeviceDpi / 96f;
            var scaledIconSize = new SizeF(this.IconSizeNumeric * scaleFactor, this.IconSizeNumeric * scaleFactor).ToSize();
            var scaledStateSize = new SizeF(16 * scaleFactor, 16 * scaleFactor).ToSize();

            var iconRect = Rectangle.Intersect(new Rectangle(e.Item.GetBounds(ItemBoundsPortion.Icon).Location, scaledIconSize), itemRect);
            iconRect.Offset(0, Math.Max(0, (itemRect.Height - scaledIconSize.Height) / 2));

            var post = this.tab[item.Index];
            this.DrawListViewItemProfileImage(e.Graphics, post, scaledIconSize, iconRect);

            var stateRect = Rectangle.Intersect(new Rectangle(new Point(iconRect.X + scaledIconSize.Width + 2, iconRect.Y), scaledStateSize), itemRect);
            this.DrawListViewItemStateIcon(e.Graphics, post, stateRect);
        }

        private void DrawListViewItemStateIcon(Graphics g, PostClass post, Rectangle stateRect)
        {
            if (post.StateIndex == -1)
                return;

            if (stateRect.Width <= 0)
                return;

            g.DrawIcon(this.GetPostStateIcon(post.StateIndex), stateRect);
        }

        private void DrawListViewItemProfileImage(Graphics g, PostClass post, Size scaledIconSize, Rectangle iconRect)
        {
            if (scaledIconSize.Width <= 0)
                return;

            var normalImageUrl = post.ImageUrl;
            if (MyCommon.IsNullOrEmpty(normalImageUrl))
                return;

            var sizeName = Twitter.DecideProfileImageSize(scaledIconSize.Width);
            var cachedImage = this.iconCache.TryGetLargerOrSameSizeFromCache(normalImageUrl, sizeName);

            if (cachedImage != null)
            {
                g.FillRectangle(Brushes.White, iconRect);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.High;
                try
                {
                    g.DrawImage(cachedImage.Image, iconRect);
                }
                catch (ArgumentException)
                {
                }
            }
            else
            {
                // キャッシュにない画像の場合は読み込みが完了してから再描画する
                async Task RefreshProfileImageLazy()
                {
                    var success = await this.LoadProfileImage(normalImageUrl, sizeName);
                    if (!success)
                        return;

                    if (this.listView.IsDisposed)
                        return;

                    if (this.listView.VirtualListSize == 0)
                        return;

                    // ロード中に index の指す行が変化している可能性がある
                    var newIndex = this.tab.IndexOf(post.StatusId);
                    if (newIndex != -1)
                        this.listView.RedrawItems(newIndex, newIndex, true);
                }

                _ = RefreshProfileImageLazy();
            }
        }

        private async Task<bool> LoadProfileImage(string normalImageUrl, string sizeName)
        {
            try
            {
                var imageUrl = Twitter.CreateProfileImageUrl(normalImageUrl, sizeName);
                await this.iconCache.DownloadImageAsync(imageUrl);

                return true;
            }
            catch (InvalidImageException)
            {
                return false;
            }
            catch (HttpRequestException)
            {
                return false;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        private Icon GetPostStateIcon(int stateIndex)
        {
            return stateIndex switch
            {
                0 => Properties.Resources.PostState00,
                1 => Properties.Resources.PostState01,
                2 => Properties.Resources.PostState02,
                3 => Properties.Resources.PostState03,
                4 => Properties.Resources.PostState04,
                5 => Properties.Resources.PostState05,
                6 => Properties.Resources.PostState06,
                7 => Properties.Resources.PostState07,
                8 => Properties.Resources.PostState08,
                9 => Properties.Resources.PostState09,
                10 => Properties.Resources.PostState10,
                11 => Properties.Resources.PostState11,
                12 => Properties.Resources.PostState12,
                13 => Properties.Resources.PostState13,
                14 => Properties.Resources.PostState14,
                _ => throw new IndexOutOfRangeException(),
            };
        }

        private Brush GetBackColorBrush(ListItemBackColor backColor)
        {
            return backColor switch
            {
                ListItemBackColor.Self => this.Theme.BrushSelf,
                ListItemBackColor.AtSelf => this.Theme.BrushAtSelf,
                ListItemBackColor.Target => this.Theme.BrushTarget,
                ListItemBackColor.AtTarget => this.Theme.BrushAtTarget,
                ListItemBackColor.AtFromTarget => this.Theme.BrushAtFromTarget,
                ListItemBackColor.AtTo => this.Theme.BrushAtTo,
                _ => this.Theme.BrushListBackcolor,
            };
        }

        private Color GetForeColor(ListItemForeColor foreColor)
        {
            return foreColor switch
            {
                ListItemForeColor.Fav => this.Theme.ColorFav,
                ListItemForeColor.Retweet => this.Theme.ColorRetweet,
                ListItemForeColor.OWL => this.Theme.ColorOWL,
                ListItemForeColor.Unread => this.Theme.ColorUnread,
                _ => this.Theme.ColorRead,
            };
        }

        private Font GetFont(ListItemFont font)
        {
            return font switch
            {
                ListItemFont.Unread => this.Theme.FontUnread,
                _ => this.Theme.FontReaded,
            };
        }

        private Font GetFontBold(ListItemFont font)
        {
            return font switch
            {
                ListItemFont.Unread => this.Theme.FontUnreadBold,
                _ => this.Theme.FontReadedBold,
            };
        }

        private void ListView_DrawItem(object sender, DrawListViewItemEventArgs e)
        {
            if (e.State == 0) return;
            var startTicks = Stopwatch.GetTimestamp();
            e.DrawDefault = false;

            var style = this.listViewCache.GetStyle(e.ItemIndex);

            Brush brs2;
            if (!e.Item.Selected) // e.ItemStateでうまく判定できない？？？
            {
                brs2 = this.GetBackColorBrush(style.BackColor);
            }
            else
            {
                // 選択中の行
                if (((Control)sender).Focused)
                    brs2 = this.Theme.BrushHighLight;
                else
                    brs2 = this.Theme.BrushDeactiveSelection;
            }
            e.Graphics.FillRectangle(brs2, e.Bounds);
            e.DrawFocusRectangle();
            this.DrawListViewItemIcon(e);

            RecordPerformanceMetric(startTicks, ref this.drawItemCount, ref this.drawItemTotalTicks, ref this.drawItemMaxTicks);
        }

        private void ListView_DrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            if (e.ItemState == 0) return;
            var startTicks = Stopwatch.GetTimestamp();

            if (e.ColumnIndex > 0)
            {
                // アイコン以外の列
                var post = this.tab[e.ItemIndex];
                var postText = post.IsDeleted
                    ? "(DELETED)"
                    : (e.Item.Tag as string ?? post.TextSingleLine);
                var style = this.listViewCache.GetStyle(e.ItemIndex);
                var font = this.GetFont(style.Font);

                RectangleF rct = e.Bounds;
                rct.Width = e.Header.Width;
                var fontHeight = font.Height;
                if (this.Use2ColumnsMode)
                {
                    rct.Y += fontHeight;
                    rct.Height -= fontHeight;
                }

                var drawLineCount = Math.Max(1, Math.DivRem((int)rct.Height, fontHeight, out var heightDiff));

                // フォントの高さの半分を足してるのは保険。無くてもいいかも。
                if (this.Use2ColumnsMode || drawLineCount > 1)
                {
                    if (heightDiff < fontHeight * 0.7)
                    {
                        // 最終行が70%以上欠けていたら、最終行は表示しない
                        rct.Height = (fontHeight * drawLineCount) - 1;
                    }
                    else
                    {
                        drawLineCount += 1;
                    }
                }

                if (rct.Width > 0)
                {
                    Color color;
                    if (e.Item.Selected)
                    {
                        color = ((Control)sender).Focused
                            ? this.Theme.ColorHighLight
                            : this.Theme.ColorUnread;
                    }
                    else
                    {
                        color = this.GetForeColor(style.ForeColor);
                    }

                    if (this.Use2ColumnsMode)
                    {
                        var rctB = e.Bounds;
                        rctB.Width = e.Header.Width;
                        rctB.Height = fontHeight;

                        var fontBold = this.GetFontBold(style.Font);

                        var formatFlags1 = TextFormatFlags.WordBreak |
                            TextFormatFlags.EndEllipsis |
                            TextFormatFlags.GlyphOverhangPadding |
                            TextFormatFlags.NoPrefix;

                        if (postText.Length > 0)
                        {
                            var mainTextStartTicks = Stopwatch.GetTimestamp();
                            TextRenderer.DrawText(
                                e.Graphics,
                                postText,
                                font,
                                Rectangle.Round(rct),
                                color,
                                formatFlags1);
                            this.RecordTextPerformanceMetric(mainTextStartTicks, isPostText: true);
                        }

                        var formatFlags2 = TextFormatFlags.SingleLine |
                            TextFormatFlags.EndEllipsis |
                            TextFormatFlags.GlyphOverhangPadding |
                            TextFormatFlags.NoPrefix;

                        var detailsTextStartTicks = Stopwatch.GetTimestamp();
                        TextRenderer.DrawText(
                            e.Graphics,
                            e.Item.SubItems[4].Text + " / " + e.Item.SubItems[1].Text + " (" + e.Item.SubItems[3].Text + ") " + e.Item.SubItems[5].Text + e.Item.SubItems[6].Text + " [" + e.Item.SubItems[7].Text + "]",
                            fontBold,
                            rctB,
                            color,
                            formatFlags2);
                        this.RecordTextPerformanceMetric(detailsTextStartTicks, isPostText: false);
                    }
                    else
                    {
                        string text;
                        if (e.ColumnIndex != 2)
                            text = e.SubItem.Text;
                        else
                            text = postText;

                        if (text.Length > 0 && drawLineCount == 1)
                        {
                            var formatFlags = TextFormatFlags.SingleLine |
                                TextFormatFlags.EndEllipsis |
                                TextFormatFlags.GlyphOverhangPadding |
                                TextFormatFlags.NoPrefix |
                                TextFormatFlags.VerticalCenter;

                            var textStartTicks = Stopwatch.GetTimestamp();
                            TextRenderer.DrawText(
                                e.Graphics,
                                text,
                                font,
                                Rectangle.Round(rct),
                                color,
                                formatFlags);
                            this.RecordTextPerformanceMetric(textStartTicks, isPostText: e.ColumnIndex == 2);
                        }
                        else if (text.Length > 0)
                        {
                            var formatFlags = TextFormatFlags.WordBreak |
                                TextFormatFlags.EndEllipsis |
                                TextFormatFlags.GlyphOverhangPadding |
                                TextFormatFlags.NoPrefix;

                            var textStartTicks = Stopwatch.GetTimestamp();
                            TextRenderer.DrawText(
                                e.Graphics,
                                text,
                                font,
                                Rectangle.Round(rct),
                                color,
                                formatFlags);
                            this.RecordTextPerformanceMetric(textStartTicks, isPostText: e.ColumnIndex == 2);
                        }
                    }
                }
            }

            RecordPerformanceMetric(startTicks, ref this.drawSubItemCount, ref this.drawSubItemTotalTicks, ref this.drawSubItemMaxTicks);
        }

        public void Dispose()
        {
            if (this.IsDisposed)
                return;

            this.UnregisterHandlers();
            this.listView.SmallImageList = null;
            this.listViewImageList.Dispose();
            this.IsDisposed = true;
        }
    }
}
