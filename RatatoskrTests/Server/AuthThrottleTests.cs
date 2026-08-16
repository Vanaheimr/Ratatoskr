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
    /// How often a stream may fail at the authentication before it is ended.
    /// </summary>
    /// <remarks>
    /// There was no limit at all: a stream could carry
    /// <c>&lt;auth/&gt;</c> after <c>&lt;auth/&gt;</c> for as long as it liked,
    /// so a password could be guessed at the speed of the network over a single
    /// connection. RFC 6120, section 13.12 names the measure.
    ///
    /// Counted per stream and not per account on purpose - a counter on the
    /// account is a lock a stranger can turn.
    /// </remarks>
    [TestFixture]
    public class AuthThrottleTests : AXMPPTests
    {

        #region TooManyFailedAttempts_EndTheStream()

        [Test]
        public async Task TooManyFailedAttempts_EndTheStream()
        {

            Server.MaxAuthenticationFailuresPerStream = 3;

            var client = await ConnectClientAsync("alice", maxReconnectAttempts: 0);

            var ended = false;
            client.Connection.OnStateChanged += (_, now) => {
                if (now != ConnectionState.Connected)
                    ended = true;
            };

            // Base64 of "\0alice\0wrong" - a PLAIN attempt that cannot succeed.
            var wrong = Convert.ToBase64String(
                            System.Text.Encoding.UTF8.GetBytes("\0alice\0wrong"));

            for (var i = 0; i < 5 && !ended; i++)
            {
                await client.SendRawAsync(
                    $"<auth xmlns='urn:ietf:params:xml:ns:xmpp-sasl' mechanism='PLAIN'>{wrong}</auth>");
                await Task.Delay(100);
            }

            await WaitFor(() => ended, "the stream to be ended after too many failures");

            Assert.That(ended, Is.True);

        }

        #endregion

    }

}
