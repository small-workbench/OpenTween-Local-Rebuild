// Modified for OpenTween Small Workbench Edition, 2026-11-21. See README.md and MODIFIED-FILES.txt.
// OpenTween - Client of Twitter
// Copyright (C) OpenTween contributors

#nullable enable

using System;
using System.Diagnostics;
using System.Windows.Forms;

namespace OpenTween.Controls
{
    /// <summary>
    /// TextBox used only by the local diagnostic build to time native input messages.
    /// </summary>
    public sealed class InputDiagnosticTextBox : TextBox
    {
        private enum MessageCategory
        {
            Ime,
            Keyboard,
            MouseSelection,
            Paint,
        }

        private readonly long[] messageCounts = new long[4];
        private readonly long[] messageTotalTicks = new long[4];
        private readonly long[] messageMaxTicks = new long[4];

        public bool NativeMessageMeasurementEnabled { get; private set; }

        public void StartNativeMessageMeasurement()
        {
            Array.Clear(this.messageCounts, 0, this.messageCounts.Length);
            Array.Clear(this.messageTotalTicks, 0, this.messageTotalTicks.Length);
            Array.Clear(this.messageMaxTicks, 0, this.messageMaxTicks.Length);
            this.NativeMessageMeasurementEnabled = true;
        }

        public string StopNativeMessageMeasurement()
        {
            this.NativeMessageMeasurementEnabled = false;
            var labels = new[] { "IMEメッセージ", "キー・文字メッセージ", "マウス選択メッセージ", "テキスト欄の描画" };
            var lines = new string[labels.Length];

            for (var index = 0; index < labels.Length; index++)
            {
                var totalMilliseconds = this.messageTotalTicks[index] * 1000d / Stopwatch.Frequency;
                var averageMilliseconds = this.messageCounts[index] == 0
                    ? 0
                    : totalMilliseconds / this.messageCounts[index];
                var maximumMilliseconds = this.messageMaxTicks[index] * 1000d / Stopwatch.Frequency;
                lines[index] = $"{labels[index]}: {this.messageCounts[index]}回、平均 {averageMilliseconds:F3} ms、最大 {maximumMilliseconds:F3} ms、合計 {totalMilliseconds:F1} ms";
            }

            return string.Join(Environment.NewLine, lines);
        }

        protected override void WndProc(ref Message m)
        {
            var category = GetMessageCategory(m.Msg);
            var startTicks = this.NativeMessageMeasurementEnabled && category is not null
                ? Stopwatch.GetTimestamp()
                : 0;

            try
            {
                base.WndProc(ref m);
            }
            finally
            {
                if (startTicks != 0 && category is { } messageCategory)
                {
                    var elapsedTicks = Stopwatch.GetTimestamp() - startTicks;
                    var index = (int)messageCategory;
                    this.messageCounts[index]++;
                    this.messageTotalTicks[index] += elapsedTicks;
                    this.messageMaxTicks[index] = Math.Max(this.messageMaxTicks[index], elapsedTicks);
                }
            }
        }

        private static MessageCategory? GetMessageCategory(int message)
        {
            return message switch
            {
                0x010D or // WM_IME_STARTCOMPOSITION
                0x010E or // WM_IME_ENDCOMPOSITION
                0x010F or // WM_IME_COMPOSITION
                0x0282 or // WM_IME_NOTIFY
                0x0286 => MessageCategory.Ime, // WM_IME_CHAR
                0x0100 or // WM_KEYDOWN
                0x0101 or // WM_KEYUP
                0x0102 or // WM_CHAR
                0x0104 or // WM_SYSKEYDOWN
                0x0105 or // WM_SYSKEYUP
                0x0106 => MessageCategory.Keyboard, // WM_SYSCHAR
                0x0200 or // WM_MOUSEMOVE
                0x0201 or // WM_LBUTTONDOWN
                0x0202 or // WM_LBUTTONUP
                0x0204 or // WM_RBUTTONDOWN
                0x0205 => MessageCategory.MouseSelection, // WM_RBUTTONUP
                0x000F => MessageCategory.Paint, // WM_PAINT
                _ => null,
            };
        }
    }
}
