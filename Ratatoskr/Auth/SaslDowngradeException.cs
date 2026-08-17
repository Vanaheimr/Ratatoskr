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

using System.Security.Authentication;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// Why a SASL announcement was refused.
/// </summary>
/// <remarks>
/// Three causes that read alike in a message and mean different things to
/// whoever has to act on them - which is the whole reason this is an enum and
/// not a boolean on the exception.
/// </remarks>
public enum SaslDowngradeCause
{

    /// <summary>
    /// The announcement stayed below the configured minimum, and most often the
    /// demand is simply wrong for this server. A stock ejabberd stores
    /// SCRAM-SHA-1 key material and announces exactly that; nothing is under
    /// attack, and the way out is to say what the server really offers.
    /// </summary>
    BelowConfiguredMinimum,

    /// <summary>
    /// The announcement stayed below what the last successful login used. This
    /// server has done better before, on this machine, with these credentials,
    /// and nothing legitimate takes a mechanism away again.
    /// </summary>
    BelowPinnedMechanism,

    /// <summary>
    /// XEP-0474: the server signed a different list of mechanisms than the one
    /// that arrived here, so something between the two changed it in flight.
    /// </summary>
    ForgedAnnouncement

}

/// <summary>
/// The announcement offered less than was demanded of it, or was not the one
/// the server sent.
/// </summary>
/// <remarks>
/// A type of its own so the caller can tell the causes apart without reading
/// the message. Only <see cref="SaslDowngradeCause.BelowConfiguredMinimum"/>
/// is answered by lowering the demand; an application that offers that advice
/// for the other two is talking its user out of the warning.
/// </remarks>
public sealed class SaslDowngradeException : AuthenticationException
{

    #region Properties

    /// <summary>
    /// The strongest mechanism the server announced - or, for
    /// <see cref="SaslDowngradeCause.ForgedAnnouncement"/>, the whole list that
    /// arrived here.
    /// </summary>
    public String              Offered    { get; }

    /// <summary>
    /// The mechanism that was demanded of it.
    /// </summary>
    public String              Demanded   { get; }

    /// <summary>
    /// Which of the three checks refused it.
    /// </summary>
    public SaslDowngradeCause  Cause      { get; }

    /// <summary>
    /// Whether lowering the configured minimum is a legitimate answer to this.
    /// True for exactly one of the three causes.
    /// </summary>
    public Boolean IsAnswerableByConfiguration
        => Cause == SaslDowngradeCause.BelowConfiguredMinimum;

    #endregion

    #region Constructor(s)

    public SaslDowngradeException(String              Message,
                                  String              Offered,
                                  String              Demanded,
                                  SaslDowngradeCause  Cause)

        : base(Message)

    {
        this.Offered   = Offered;
        this.Demanded  = Demanded;
        this.Cause     = Cause;
    }

    #endregion

}
