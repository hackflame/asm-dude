﻿// The MIT License (MIT)
//
// Copyright (c) 2016 H.J. Lebbink
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:

// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.

// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Text;

namespace AsmDude.CodeFolding {

    internal sealed class CodeFoldingTagger : ITagger<IOutliningRegionTag> {

        #region Private Fields
        private string startHide = Settings.Default.CodeFolding_BeginTag;     //the characters that start the outlining region
        private string endHide = Settings.Default.CodeFolding_EndTag;       //the characters that end the outlining region

        private readonly ITextBuffer _buffer;
        private ITextSnapshot _snapshot;
        private List<Region> _regions;
        #endregion Private Fields

        public CodeFoldingTagger(ITextBuffer buffer) {
            //Debug.WriteLine("INFO:OutliningTagger: constructor");
            this._buffer = buffer;
            this._snapshot = buffer.CurrentSnapshot;
            this._regions = new List<Region>();
            this.ReParse();
            this._buffer.Changed += BufferChanged;
        }

        public IEnumerable<ITagSpan<IOutliningRegionTag>> GetTags(NormalizedSnapshotSpanCollection spans) {
            if (spans.Count == 0) {
                yield break;
            }
            if (Settings.Default.CodeFolding_On) {
                //Debug.WriteLine("INFO: GetTags: entering");

                SnapshotSpan entire = new SnapshotSpan(spans[0].Start, spans[spans.Count - 1].End).TranslateTo(this._snapshot, SpanTrackingMode.EdgeExclusive);
                int startLineNumber = entire.Start.GetContainingLine().LineNumber;
                int endLineNumber = entire.End.GetContainingLine().LineNumber;

                foreach (Region region in this._regions) {
                    if ((region.StartLine <= endLineNumber) && (region.EndLine >= startLineNumber)) {

                        ITextSnapshotLine startLine = this._snapshot.GetLineFromLineNumber(region.StartLine);
                        ITextSnapshotLine endLine = this._snapshot.GetLineFromLineNumber(region.EndLine);

                        var replacement = getRegionDescription(startLine.GetText());
                        var hover = getHoverText(region.StartLine + 1, region.EndLine - 1, this._snapshot);

                        yield return new TagSpan<IOutliningRegionTag>(
                            new SnapshotSpan(startLine.Start + region.StartOffset, endLine.End),
                            new OutliningRegionTag(false, false, replacement, hover));
                    }
                }
            }
        }
        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        #region Private Methods

        private string getRegionDescription(string line) {
            int startPos = line.IndexOf(startHide[0]) + startHide.Length + 1;
            if (startPos < line.Length) {
                string description = line.Substring(startPos).Trim();
                if (description.Length > 0) {
                    return description;
                }
            }
            return line.Trim();
        }
        private string getHoverText(int begin, int end, ITextSnapshot snapshot) {

            // TODO make hoverText a textobject with smaller fontsize

            string str = "";

            if (begin < end) {
                str = snapshot.GetLineFromLineNumber(begin).GetText();
            }
            for (int i = begin + 1; i < end; ++i) {
                str += "\n" + snapshot.GetLineFromLineNumber(i).GetText();
            }
            return str;
        }

        private void BufferChanged(object sender, TextContentChangedEventArgs e) {
            // If this isn't the most up-to-date version of the buffer, then ignore it for now (we'll eventually get another change event).
            if (e.After != _buffer.CurrentSnapshot) {
                return;
            }
            this.ReParse();
        }

        private void ReParse() {
            //Debug.WriteLine("INFO:OutliningTagger:ReParse: entering");

            ITextSnapshot newSnapshot = _buffer.CurrentSnapshot;
            List<Region> newRegions = new List<Region>();

            //keep the current (deepest) partial region, which will have
            // references to any parent partial regions.
            PartialRegion currentRegion = null;

            foreach (var line in newSnapshot.Lines) {
                int regionStart = -1;
                string text = line.GetText();

                //lines that contain a "[" denote the start of a new region.
                if ((regionStart = text.IndexOf(startHide, StringComparison.Ordinal)) != -1) {
                    int currentLevel = (currentRegion != null) ? currentRegion.Level : 1;
                    int newLevel;
                    if (!TryGetLevel(text, regionStart, out newLevel))
                        newLevel = currentLevel + 1;

                    //levels are the same and we have an existing region;
                    //end the current region and start the next
                    if (currentLevel == newLevel && currentRegion != null) {
                        newRegions.Add(new Region() {
                            Level = currentRegion.Level,
                            StartLine = currentRegion.StartLine,
                            StartOffset = currentRegion.StartOffset,
                            EndLine = line.LineNumber
                        });

                        currentRegion = new PartialRegion() {
                            Level = newLevel,
                            StartLine = line.LineNumber,
                            StartOffset = regionStart,
                            PartialParent = currentRegion.PartialParent
                        };
                    }
                    //this is a new (sub)region
                    else {
                        currentRegion = new PartialRegion() {
                            Level = newLevel,
                            StartLine = line.LineNumber,
                            StartOffset = regionStart,
                            PartialParent = currentRegion
                        };
                    }
                }
                //lines that contain "]" denote the end of a region
                else if ((regionStart = text.IndexOf(endHide, StringComparison.Ordinal)) != -1) {
                    int currentLevel = (currentRegion != null) ? currentRegion.Level : 1;
                    int closingLevel;
                    if (!TryGetLevel(text, regionStart, out closingLevel))
                        closingLevel = currentLevel;

                    //the regions match
                    if (currentRegion != null &&
                        currentLevel == closingLevel) {
                        newRegions.Add(new Region() {
                            Level = currentLevel,
                            StartLine = currentRegion.StartLine,
                            StartOffset = currentRegion.StartOffset,
                            EndLine = line.LineNumber
                        });

                        currentRegion = currentRegion.PartialParent;
                    }
                }
            }

            //determine the changed span, and send a changed event with the new spans
            List<Span> oldSpans =
                new List<Span>(this._regions.Select(r => AsSnapshotSpan(r, this._snapshot)
                    .TranslateTo(newSnapshot, SpanTrackingMode.EdgeExclusive)
                    .Span));
            List<Span> newSpans = new List<Span>(newRegions.Select(r => AsSnapshotSpan(r, newSnapshot).Span));

            NormalizedSpanCollection oldSpanCollection = new NormalizedSpanCollection(oldSpans);
            NormalizedSpanCollection newSpanCollection = new NormalizedSpanCollection(newSpans);

            //the changed regions are regions that appear in one set or the other, but not both.
            NormalizedSpanCollection removed = NormalizedSpanCollection.Difference(oldSpanCollection, newSpanCollection);

            int changeStart = int.MaxValue;
            int changeEnd = -1;

            if (removed.Count > 0) {
                changeStart = removed[0].Start;
                changeEnd = removed[removed.Count - 1].End;
            }

            if (newSpans.Count > 0) {
                changeStart = Math.Min(changeStart, newSpans[0].Start);
                changeEnd = Math.Max(changeEnd, newSpans[newSpans.Count - 1].End);
            }

            this._snapshot = newSnapshot;
            this._regions = newRegions;

            if (changeStart <= changeEnd) {
                ITextSnapshot snap = this._snapshot;
                if (this.TagsChanged != null)
                    this.TagsChanged(this, new SnapshotSpanEventArgs(
                        new SnapshotSpan(this._snapshot, Span.FromBounds(changeStart, changeEnd))));
            }
            //Debug.WriteLine("INFO:OutliningTagger:ReParse: leaving");
        }

        private static bool TryGetLevel(string text, int startIndex, out int level) {
            level = -1;
            if (text.Length > startIndex + 3) {
                if (int.TryParse(text.Substring(startIndex + 1), out level))
                    return true;
            }

            return false;
        }

        private static SnapshotSpan AsSnapshotSpan(Region region, ITextSnapshot snapshot) {
            var startLine = snapshot.GetLineFromLineNumber(region.StartLine);
            var endLine = (region.StartLine == region.EndLine) ? startLine : snapshot.GetLineFromLineNumber(region.EndLine);
            return new SnapshotSpan(startLine.Start + region.StartOffset, endLine.End);
        }

        class PartialRegion {
            public int StartLine { get; set; }
            public int StartOffset { get; set; }
            public int Level { get; set; }
            public PartialRegion PartialParent { get; set; }
        }

        class Region : PartialRegion {
            public int EndLine { get; set; }
        }

        #endregion Private Methods
    }
}
