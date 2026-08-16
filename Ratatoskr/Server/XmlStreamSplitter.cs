/*
 * Copyright (c) 2010-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of Ratatoskr <https://www.github.com/Vanaheimr/Ratatoskr>
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Server
{

    /// <summary>
    /// Takes the character stream of an XMPP stream (RFC 6120) apart into
    /// individual frames: the stream header, then every stanza, lastly the end
    /// of the stream.
    /// </summary>
    /// <remarks>
    /// Over WebSocket this comes for free - a frame is an element. Over TCP a
    /// stream arrives in which one stanza can be spread over any number of
    /// reads and several stanzas may sit inside one. Without this splitting TCP
    /// <b>seems to work</b> as long as the packets happen to fall on element
    /// boundaries - that is, on localhost in a test almost always, and in
    /// operation then no longer. That is why it stands here as a building block
    /// of its own, checked for itself, and not as a manoeuvre inside the
    /// receive loop.
    ///
    /// Deliberately <b>not</b> an XML parser: the stream header is an open tag
    /// and would not be well-formed taken by itself, and a parser over the
    /// whole stream would have to build the entire stream up as one document.
    /// What is needed here is only the art of finding element boundaries -
    /// together with the traps that count while doing so: quotation marks
    /// inside which a <c>&gt;</c> may stand, CDATA, comments.
    ///
    /// This class holds no state about well-formedness; it does not check
    /// whether tags match one another. A stream with wrongly nested names is
    /// split, not refused - judging that is the business of the layer above.
    /// </remarks>
    public sealed class XmlStreamSplitter
    {

        #region Data

        /// <summary>
        /// The largest single frame this splitter will assemble, in characters.
        /// </summary>
        /// <remarks>
        /// <c>rest</c> is what has arrived and does not yet form a complete
        /// element, and it grew without any bound at all. A peer that opens a
        /// tag and then simply keeps sending - never closing it - made this
        /// buffer grow until the machine gave out, and it costs them nothing
        /// but the sending. RFC 6120, section 13.12 asks for a limit for
        /// exactly this reason.
        ///
        /// The value matches <c>XMPPConnection.MaxStanzaBytes</c>, in
        /// characters rather than bytes - the splitter has already been handed
        /// decoded text and can no longer count what it cost on the wire. For a
        /// limit against growth that is the right side to err on: a character
        /// is at most as many bytes again, never fewer.
        /// </remarks>
        public const Int32 MaxFrameLength = 4 * 1024 * 1024;

        private String   rest      = "";
        private Boolean  rootSeen;

        #endregion

        #region OverlongFrameException

        /// <summary>
        /// Thrown when a peer announces an element and does not end it within
        /// <see cref="MaxFrameLength"/>.
        /// </summary>
        /// <remarks>
        /// An exception and not a silent discard. Discarding would leave the
        /// stream standing in the middle of an element nobody can place, and
        /// everything after it would be read as the tail of something that was
        /// thrown away. Whoever sends this is broken or hostile, and either way
        /// the connection is over.
        /// </remarks>
        public sealed class OverlongFrameException : Exception
        {
            public OverlongFrameException(Int32 Length)
                : base($"The peer has sent {Length} characters without completing an element; " +
                       $"at most {MaxFrameLength} are assembled.")
            { }
        }

        #endregion

        #region Push(text)

        /// <summary>
        /// Takes in the next piece of the stream and delivers all the frames
        /// that have become complete with it.
        /// </summary>
        /// <remarks>
        /// The first frame delivered is the stream header - that is, the
        /// <b>open</b> <c>&lt;stream:stream ...&gt;</c> tag without its
        /// children. After that follows one frame per stanza, lastly
        /// <c>&lt;/stream:stream&gt;</c>.
        /// </remarks>
        public IReadOnlyList<String> Push(String text)
        {

            rest += text;

            var frames = new List<String>();

            while (true)
            {

                var start = SkipProlog(rest);

                if (start >= rest.Length)
                {
                    rest = "";
                    break;
                }

                var end = ScanOne(rest, start, stopAfterOpenTag: !rootSeen);

                if (end < 0)
                {

                    // Still incomplete - what was already skipped may go.
                    rest = rest[start..];

                    // And what is left has to stay within bounds. Checked here
                    // and not at the top: only an element that will not end is
                    // a problem, and up here it is known that this one has not
                    // ended.
                    if (rest.Length > MaxFrameLength)
                        throw new OverlongFrameException(rest.Length);

                    break;

                }

                frames.Add(rest[start..end]);
                rest      = rest[end..];
                rootSeen  = true;

            }

            return frames;

        }

        #endregion

        #region Reset()

        /// <summary>
        /// Starts over - the next frame is a stream header again.
        /// </summary>
        /// <remarks>
        /// After a successful SASL the stream begins anew (RFC 6120,
        /// section 6.4.6), and as a fresh XML document at that. Without this
        /// cut the splitter would take the second <c>&lt;stream:stream&gt;</c>
        /// for a child element of the first and wait until the end of time for
        /// its closing tag - the restart would never arrive above, and the
        /// connection would stand still without anything looking broken.
        ///
        /// Remains that were begun are discarded in the process. That is
        /// intentional: after the restart the old state no longer holds anyway,
        /// and a peer that has left something half-finished in the buffer is
        /// not keeping to the order.
        /// </remarks>
        public void Reset()
        {
            rest      = "";
            rootSeen  = false;
        }

        #endregion

        #region (private static) SkipProlog(s)

        /// <summary>
        /// Skips whitespace, XML declarations and comments between two
        /// elements.
        /// </summary>
        /// <remarks>
        /// Both are permitted at the top level and are without meaning for the
        /// protocol. Were they passed on as frames, the layer above would take
        /// the XML declaration for the stream header.
        /// </remarks>
        private static Int32 SkipProlog(String s)
        {

            var i = 0;

            while (i < s.Length)
            {

                while (i < s.Length && Char.IsWhiteSpace(s[i]))
                    i++;

                if (Match(s, i, "<?"))
                {

                    var e = s.IndexOf("?>", i + 2, StringComparison.Ordinal);

                    if (e < 0)
                        return i;

                    i = e + 2;
                    continue;

                }

                if (Match(s, i, "<!--"))
                {

                    var e = s.IndexOf("-->", i + 4, StringComparison.Ordinal);

                    if (e < 0)
                        return i;

                    i = e + 3;
                    continue;

                }

                break;

            }

            return i;

        }

        #endregion

        #region (private static) ScanOne(s, start, stopAfterOpenTag)

        /// <summary>
        /// Searches for the end of exactly one element from
        /// <paramref name="start"/> on.
        /// </summary>
        /// <param name="stopAfterOpenTag">
        /// For the stream header: stop after the opening tag instead of waiting
        /// for its closing one. The root element is only closed at the end of
        /// the stream - waiting for that would mean never delivering a frame.
        /// </param>
        /// <returns>The index behind the element, or -1 when still incomplete.</returns>
        private static Int32 ScanOne(String s, Int32 start, Boolean stopAfterOpenTag)
        {

            var i      = start;
            var depth  = 0;

            while (i < s.Length)
            {

                if (s[i] != '<')
                {
                    i++;
                    continue;
                }

                if (Match(s, i, "<!--"))
                {

                    var e = s.IndexOf("-->", i + 4, StringComparison.Ordinal);

                    if (e < 0)
                        return -1;

                    i = e + 3;
                    continue;

                }

                // Inside CDATA anything may stand, including '<' and '>'.
                if (Match(s, i, "<![CDATA["))
                {

                    var e = s.IndexOf("]]>", i + 9, StringComparison.Ordinal);

                    if (e < 0)
                        return -1;

                    i = e + 3;
                    continue;

                }

                if (Match(s, i, "<?"))
                {

                    var e = s.IndexOf("?>", i + 2, StringComparison.Ordinal);

                    if (e < 0)
                        return -1;

                    i = e + 2;
                    continue;

                }

                var closing = Match(s, i, "</");
                var j       = i + 1;
                var quote   = '\0';
                var empty   = false;

                while (j < s.Length)
                {

                    var c = s[j];

                    if (quote != '\0')
                    {
                        if (c == quote)
                            quote = '\0';
                        j++;
                        continue;
                    }

                    if (c is '\'' or '"')
                    {
                        quote = c;
                        j++;
                        continue;
                    }

                    // A '>' inside an attribute value is valid XML and does not
                    // end the tag - hence only here, outside the quotation
                    // marks.
                    if (c == '>')
                    {

                        var k = j - 1;

                        while (k > i && Char.IsWhiteSpace(s[k]))
                            k--;

                        empty = s[k] == '/';
                        break;

                    }

                    j++;

                }

                // The tag has not been read to its end yet.
                if (j >= s.Length)
                    return -1;

                i = j + 1;

                if (closing)
                {

                    depth--;

                    // A </stream:stream> as the first element lands here too:
                    // the depth becomes negative, and the frame is complete.
                    if (depth <= 0)
                        return i;

                }

                else if (empty)
                {
                    if (depth == 0)
                        return i;
                }

                else
                {

                    depth++;

                    if (stopAfterOpenTag)
                        return i;

                }

            }

            return -1;

        }

        #endregion

        #region (private static) Match(s, i, text)

        private static Boolean Match(String s, Int32 i, String text)
            => i + text.Length <= s.Length &&
               String.CompareOrdinal(s, i, text, 0, text.Length) == 0;

        #endregion

    }

}
