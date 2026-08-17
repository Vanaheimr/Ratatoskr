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
    /// In which order a message meets the two checks that apply to it.
    /// </summary>
    /// <remarks>
    /// The unit tests beside this one measure whether the unwrapping refuses a
    /// forged carbon. This one measures the thing the finding was actually
    /// about: that the refusal is <b>reached</b>. An encrypted carbon was
    /// handled by a branch that stood before the carbon check and unwrapped on
    /// its own - so the check existed, was correct, and was jumped over on the
    /// one path where the payload got decrypted.
    ///
    /// Which is why the stanza below carries a parseable <c>&lt;encrypted/&gt;</c>
    /// element. Without one the old branch fell through of its own accord and
    /// the spoofing was reported anyway; with one it returned, and the report
    /// never came.
    /// </remarks>
    [TestFixture]
    public class CarbonOrderTests : AXMPPTests
    {

        #region AForgedEncryptedCarbon_ReachesTheSpoofingCheck()

        [Test]
        public async Task AForgedEncryptedCarbon_ReachesTheSpoofingCheck()
        {

            var alice = await ConnectClientAsync("alice");

            await alice.EnableOmemoAsync();

            String? spoofing  = null;
            var     decrypted = false;

            alice.Connection.OnSpoofingAttempt  += (timestamp, sender, message, ct) => { spoofing  = message; return Task.CompletedTask; };
            alice.Connection.OnEncryptedMessage += (timestamp, sender, _, _, ct)   => { decrypted = true; return Task.CompletedTask; };

            // A stranger's stanza, built the way a carbon is built, with an
            // <encrypted/> that parses. Everything about it is in order except
            // the one thing that matters: it did not come from our own account.
            await alice.Connection.ProcessStanzaAsync(
                $"<message from='mallory@{Server.Domain}' to='{alice.FullJid}'>" +
                  $"<received xmlns='{CarbonManager.Namespace}'>" +
                    "<forwarded xmlns='urn:xmpp:forward:0'>" +
                      $"<message from='bob@{Server.Domain}' to='alice@{Server.Domain}' type='chat'>" +
                        "<encrypted xmlns='urn:xmpp:omemo:2'>" +
                          "<header sid='4711'>" +
                            $"<keys jid='alice@{Server.Domain}'>" +
                              $"<key rid='{alice.Connection.Omemo!.Identity.DeviceId}'>AAAA</key>" +
                            "</keys>" +
                          "</header>" +
                          "<payload>AAAA</payload>" +
                        "</encrypted>" +
                      "</message>" +
                    "</forwarded>" +
                  "</received>" +
                "</message>");

            await WaitFor(() => spoofing is not null, "the forged carbon to be reported");

            Assert.Multiple(() =>
            {
                Assert.That(spoofing,  Does.Contain("mallory"));
                Assert.That(decrypted, Is.False,
                            "Nothing out of a stranger's carbon may reach the application.");
            });

        }

        #endregion

    }

}
