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
/// XEP-0004: A field of a data form.
/// </summary>
/// <param name="Var">The name of the field (<c>var</c>).</param>
/// <param name="Type">
/// The field type, or null when the form gives none. It is needed because
/// XEP-0115, section 5.4 lets a <c>FORM_TYPE</c> count only when it is
/// <c>hidden</c>.
/// </param>
/// <param name="Values">The values of the field, in the order of the form.</param>
public sealed record DiscoField(String                 Var,
                                String?                Type,
                                IReadOnlyList<String>  Values)
{

    /// <summary>
    /// The name of the field that carries the form type.
    /// </summary>
    public const String FormTypeVar = "FORM_TYPE";

    /// <summary>
    /// The field type XEP-0115 demands for <see cref="FormTypeVar"/>.
    /// </summary>
    public const String HiddenType  = "hidden";

    /// <summary>
    /// Is this a valid FORM_TYPE field (XEP-0115, section 5.4)?
    /// </summary>
    public Boolean IsFormType

        => Var  == FormTypeVar &&
           Type == HiddenType;

}
