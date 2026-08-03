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

#region Usings

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// RFC 6120, section 8.3 (stanza errors) and section 4.9 (stream errors),
    /// without a network.
    /// </summary>
    [TestFixture]
    public class StanzaErrorTests
    {

        #region Parse_ReadsTypeConditionAndText()

        /// <summary>
        /// The complete example from section 8.3.2.
        /// </summary>
        [Test]
        public void Parse_ReadsTypeConditionAndText()
        {

            var ok = StanzaError.TryParse(
                         "<iq type='error' id='1' from='example.org'>" +
                         "<error type='modify' by='example.org'>" +
                         "<bad-request xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/>" +
                         "<text xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'>That was nothing.</text>" +
                         "</error></iq>",
                         out var error);

            Assert.Multiple(() =>
            {
                Assert.That(ok,                Is.True);
                Assert.That(error!.Type,       Is.EqualTo(StanzaErrorType.Modify));
                Assert.That(error!.Condition,  Is.EqualTo("bad-request"));
                Assert.That(error!.Text,       Is.EqualTo("That was nothing."));
                Assert.That(error!.By,         Is.EqualTo("example.org"));
            });

        }

        #endregion

        #region Parse_MapsAllErrorTypes()

        /// <summary>
        /// All five error types from section 8.3.2.
        /// </summary>
        [Test]
        [TestCase("auth",     StanzaErrorType.Auth)]
        [TestCase("cancel",   StanzaErrorType.Cancel)]
        [TestCase("continue", StanzaErrorType.Continue)]
        [TestCase("modify",   StanzaErrorType.Modify)]
        [TestCase("wait",     StanzaErrorType.Wait)]
        public void Parse_MapsAllErrorTypes(String attribute, StanzaErrorType expected)
        {

            StanzaError.TryParse(
                $"<message type='error'><error type='{attribute}'>" +
                "<forbidden xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/>" +
                "</error></message>",
                out var error);

            Assert.That(error!.Type, Is.EqualTo(expected));

        }

        #endregion

        #region Parse_FallsBackToCancelOnUnknownType()

        /// <summary>
        /// With a missing or unknown error type <c>cancel</c> is the most
        /// cautious assumption: it leads to no repeated attempt.
        /// </summary>
        [Test]
        [TestCase("<error><forbidden xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/></error>")]
        [TestCase("<error type='completely-new'><forbidden xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/></error>")]
        public void Parse_FallsBackToCancelOnUnknownType(String errorElement)
        {

            StanzaError.TryParse($"<iq type='error'>{errorElement}</iq>", out var error);

            Assert.That(error!.Type, Is.EqualTo(StanzaErrorType.Cancel));

        }

        #endregion

        #region Parse_KeepsUnknownConditions()

        /// <summary>
        /// The condition stays a string, so that future and
        /// application-specific conditions come through unfalsified too.
        /// </summary>
        [Test]
        public void Parse_KeepsUnknownConditions()
        {

            StanzaError.TryParse(
                "<iq type='error'><error type='cancel'>" +
                "<something-new xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/>" +
                "</error></iq>",
                out var error);

            Assert.That(error!.Condition, Is.EqualTo("something-new"));

        }

        #endregion

        #region Parse_DoesNotMistakeTextForTheCondition()

        /// <summary>
        /// <c>&lt;text/&gt;</c> lies in the same namespace as the condition and
        /// must not be read as one - not even when it stands first.
        /// </summary>
        [Test]
        public void Parse_DoesNotMistakeTextForTheCondition()
        {

            StanzaError.TryParse(
                "<iq type='error'><error type='cancel'>" +
                "<text xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'>The text first.</text>" +
                "<gone xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/>" +
                "</error></iq>",
                out var error);

            Assert.Multiple(() =>
            {
                Assert.That(error!.Condition, Is.EqualTo("gone"));
                Assert.That(error!.Text,      Is.EqualTo("The text first."));
            });

        }

        #endregion

        #region Parse_ReturnsFalseWithoutErrorElement()

        /// <summary>
        /// A stanza without an error element is no error.
        /// </summary>
        [Test]
        [TestCase("<message type='chat'><body>Hello</body></message>")]
        [TestCase("<iq type='result' id='1'/>")]
        [TestCase("")]
        public void Parse_ReturnsFalseWithoutErrorElement(String stanza)
        {
            Assert.That(StanzaError.TryParse(stanza, out _), Is.False);
        }

        #endregion

        #region StreamError_ReadsConditionAndText()

        /// <summary>
        /// Stream errors per section 4.9, including the usual stream prefix.
        /// </summary>
        [Test]
        public void StreamError_ReadsConditionAndText()
        {

            var ok = StreamError.TryParse(
                         "<stream:error xmlns:stream='http://etherx.jabber.org/streams'>" +
                         "<conflict xmlns='urn:ietf:params:xml:ns:xmpp-streams'/>" +
                         "<text xmlns='urn:ietf:params:xml:ns:xmpp-streams'>Resource twice.</text>" +
                         "</stream:error>",
                         out var error);

            Assert.Multiple(() =>
            {
                Assert.That(ok,                Is.True);
                Assert.That(error!.Condition,  Is.EqualTo("conflict"));
                Assert.That(error!.Text,       Is.EqualTo("Resource twice."));
            });

        }

        #endregion

        #region StreamError_SeparatesRecoverableFromFatal()

        /// <summary>
        /// The distinction decides whether a reconnect is attempted. With the
        /// final conditions it would run into the same refusal and produce a
        /// loop.
        /// </summary>
        [Test]
        [TestCase("system-shutdown",          true)]
        [TestCase("connection-timeout",       true)]
        [TestCase("resource-constraint",      true)]
        [TestCase("internal-server-error",    true)]
        [TestCase("reset",                    true)]
        [TestCase("conflict",                 false)]
        [TestCase("host-unknown",             false)]
        [TestCase("not-authorized",           false)]
        [TestCase("policy-violation",         false)]
        [TestCase("unsupported-version",      false)]
        [TestCase("see-other-host",           false)]
        public void StreamError_SeparatesRecoverableFromFatal(String condition, Boolean recoverable)
        {

            StreamError.TryParse(
                "<stream:error xmlns:stream='http://etherx.jabber.org/streams'>" +
                $"<{condition} xmlns='urn:ietf:params:xml:ns:xmpp-streams'/>" +
                "</stream:error>",
                out var error);

            Assert.That(error!.IsRecoverable, Is.EqualTo(recoverable),
                        $"'{condition}' is classified wrongly.");

        }

        #endregion

        #region StreamError_ReturnsFalseForOtherStanzas()

        /// <summary>
        /// A stanza error is no stream error.
        /// </summary>
        [Test]
        [TestCase("<message type='error'><error type='cancel'/></message>")]
        [TestCase("<iq type='error' id='1'/>")]
        [TestCase("<presence/>")]
        public void StreamError_ReturnsFalseForOtherStanzas(String stanza)
        {
            Assert.That(StreamError.TryParse(stanza, out _), Is.False);
        }

        #endregion

    }

}
