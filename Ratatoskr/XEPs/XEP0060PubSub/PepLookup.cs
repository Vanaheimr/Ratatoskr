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

using System.Xml.Linq;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// What asking an account's server for a PEP item produced - and, when it
/// produced nothing, whether that nothing is an answer or the absence of one.
/// </summary>
/// <remarks>
/// <b>Two kinds of nothing, and telling them apart is a security decision.</b>
/// A node that is not there is a statement: this account publishes no such
/// thing, and a caller may act on it. A request nobody answered is a statement
/// about nothing at all - and whoever can hold up or drop a single stanza can
/// produce it whenever they like.
///
/// OMEMO is where the difference shows. A sender fetches the recipient's device
/// list before every encrypted message, and a client that reads "no answer" as
/// "no device list" writes in the clear exactly when somebody has arranged for
/// the answer not to arrive. That is the downgrade the whole of OMEMO exists to
/// prevent, and it looks like an attack from neither end: the sender sees an
/// ordinary message leave, the recipient sees an ordinary message arrive.
/// </remarks>
public enum PepLookup
{

    /// <summary>
    /// The server answered with a result. Whether that result carries the item
    /// is for whoever reads it to say.
    /// </summary>
    Published,

    /// <summary>
    /// It is not there, and the server said so - an <c>item-not-found</c>, or a
    /// result with nothing in it. <b>An answer</b>, and the only kind of
    /// nothing a caller may draw a conclusion from.
    /// </summary>
    Absent,

    /// <summary>
    /// Nothing was learnt: no answer came, the answer was about the asking
    /// rather than about the thing asked for, or what came back could not be
    /// read. <b>Not the absence of the thing</b> - see the remark on this enum.
    /// </summary>
    Unknown

}

/// <summary>
/// Reads an answer to a PEP fetch for what it says about the item that was
/// asked for.
/// </summary>
public static class PepAnswer
{

    /// <summary>
    /// What this answer says about the item that was asked for.
    /// </summary>
    /// <param name="Answer">The answer stanza, or null when none came.</param>
    /// <returns>
    /// <see cref="PepLookup.Published"/> for a result - whether it carries an
    /// item is for the caller to read, and a caller that finds none has an
    /// <see cref="PepLookup.Absent"/> of its own to report;
    /// <see cref="PepLookup.Absent"/> for a refusal that names the item as not
    /// there; <see cref="PepLookup.Unknown"/> for everything else.
    /// </returns>
    /// <remarks>
    /// <b>Three conditions are about the item. The rest are about the
    /// asking.</b> <c>item-not-found</c> is what a server answers for a node
    /// that is not there (XEP-0060, section 6.5.9.9); <c>service-unavailable</c>
    /// and <c>feature-not-implemented</c> are what a server with no PEP at all
    /// answers, and an account on such a server has no device list in the same
    /// way as one on a server that does have PEP.
    ///
    /// Everything else is the asking having failed, whatever it names -
    /// <c>internal-server-error</c>, <c>remote-server-timeout</c>,
    /// <c>recipient-unavailable</c>, <c>undefined-condition</c>. So is
    /// <c>forbidden</c>, and that one is worth saying out loud: <b>a node this
    /// side may not read is a node that is there.</b> Counting it as "publishes
    /// nothing" would turn somebody else's access rule into this side's licence
    /// to write in the clear.
    ///
    /// A <c>type='wait'</c> error says the same request may work later (RFC
    /// 6120, section 8.3.2), so whatever condition it carries, it is not a
    /// statement about what is there.
    /// </remarks>
    public static PepLookup WhatItSays(XElement? Answer)
    {

        // No answer at all: a slow server, a lost stanza, a timeout that
        // expired - the case this enum exists for.
        if (Answer is null)
            return PepLookup.Unknown;

        if (Answer.Attr("type") == "result")
            return PepLookup.Published;

        // An error whose error element cannot be read says nothing either.
        if (!StanzaError.TryParse(Answer.ToString(), out var error) ||
            error is null)
            return PepLookup.Unknown;

        if (error.Type == StanzaErrorType.Wait)
            return PepLookup.Unknown;

        return error.Condition switch {
                   "item-not-found"           => PepLookup.Absent,
                   "service-unavailable"      => PepLookup.Absent,
                   "feature-not-implemented"  => PepLookup.Absent,
                   _                          => PepLookup.Unknown
               };

    }

}
