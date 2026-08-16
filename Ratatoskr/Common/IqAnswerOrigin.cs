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

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// Whether an IQ answer may belong to the request it names.
/// </summary>
/// <remarks>
/// <b>An identifier is not an assignment.</b> This side picks it, it is short
/// and countable - <c>pep-1</c>, <c>roster1</c>, <c>disco-info-2</c> - and it
/// travels in the clear; the full JID it is addressed to goes out with every
/// presence. Anybody who may write to this client can therefore name a question
/// that is currently in flight and answer it ahead of whoever was asked.
///
/// The rule lives here and not in the three places that need it, because three
/// copies of a security check are three chances for two of them to be wrong.
/// <see cref="XMPPConnection"/> keeps the general pending requests,
/// <c>DiscoManager</c> and <c>PingManager</c> keep their own - all three ask
/// the same question of the same answer.
/// </remarks>
internal static class IqAnswerOrigin
{

    #region MayBelongTo(ExpectedFrom, From, OwnBareJid)

    /// <summary>
    /// May an answer carrying this sender belong to a request addressed there?
    /// </summary>
    /// <param name="ExpectedFrom">
    /// Whom the request was addressed to, or null when it carried no <c>to</c>
    /// and thereby went to one's own server (RFC 6120, section 10.3.3).
    /// </param>
    /// <param name="From">The sender of the answer, as the stanza names it.</param>
    /// <param name="OwnBareJid">
    /// One's own account, for the two cases that are about it. Null where the
    /// caller does not know it - the check is then strictly narrower, never
    /// wider.
    /// </param>
    public static Boolean MayBelongTo(String? ExpectedFrom,
                                      String? From,
                                      String? OwnBareJid)
    {

        // No sender named is one's own server, and it can be nothing else. RFC
        // 6120, section 8.1.2.1 obliges the server to write the sender's full
        // JID onto every stanza it takes from a client, overriding whatever
        // stood there. A peer therefore cannot produce this; what they send
        // arrives carrying their own address, which is the case the comparisons
        // below catch. And against the server itself this never protected and
        // could not: it routes everything and may put any address on top. That
        // party is fended off with fingerprints, one layer up.
        if (From is null)
            return true;

        // One's own server naming itself is the same case with an address on
        // it, and it has to pass whoever was asked. The server answers instead
        // of the addressee whenever it cannot reach them - an unknown domain, a
        // refused route - and what comes back then is the error saying so.
        // Refusing it fends off nothing and turns a remote-server-not-found
        // into a silence that runs out the caller's timeout.
        if (OwnBareJid is not null &&
            String.Equals(From, DomainOf(OwnBareJid), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Nobody was addressed, so the request went to one's own server, which
        // then reports under the account's own bare JID.
        if (ExpectedFrom is null)
            return OwnBareJid is not null && SameEntity(From, OwnBareJid);

        // Somebody was addressed, and only they may answer. Compared bare, the
        // way this codebase compares JIDs everywhere: a request to a full JID
        // may be answered by another resource of the same account, and that is
        // the same person, not a stranger.
        return SameEntity(From, ExpectedFrom);

    }

    #endregion

    #region (private) SameEntity(One, Other) / DomainOf(Jid)

    private static Boolean SameEntity(String? One, String? Other)

        => One is not null && Other is not null &&
           JidUtilities.Bare(One) == JidUtilities.Bare(Other);

    private static String DomainOf(String Jid)
    {

        var bare = JidUtilities.Bare(Jid);
        var at   = bare.IndexOf('@');

        return at >= 0 ? bare[(at + 1)..] : bare;

    }

    #endregion

}
