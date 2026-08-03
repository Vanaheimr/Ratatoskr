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
/// XEP-0128: A data form at a disco#info answer - the extended information an
/// entity gives about itself.
/// </summary>
/// <remarks>
/// What is stored is what stood in the form, unfiltered and in the order
/// found. Which forms count and how they are sorted is decided by XEP-0115,
/// sections 5.1 and 5.4 - and that stands where these rules belong, in the
/// <see cref="EntityCapsManager"/>. A parser that sorts things out already
/// takes the ground away from the check.
/// </remarks>
/// <param name="Fields">The fields of the form, FORM_TYPE included.</param>
public sealed record DiscoForm(IReadOnlyList<DiscoField> Fields)
{

    /// <summary>
    /// The FORM_TYPE field, provided there is one and it carries the demanded
    /// type <c>hidden</c> (XEP-0115, section 5.4).
    /// </summary>
    public DiscoField? FormTypeField

        // "field" is a keyword inside a property accessor from C# 14 on.
        => Fields.FirstOrDefault(f => f.IsFormType);

    /// <summary>
    /// The form type, or null when the form carries no valid one - such a form
    /// does not go into the verification string per XEP-0115, section 5.4.
    /// </summary>
    public String? FormType

        => FormTypeField?.Values.FirstOrDefault();


    #region (static) Of(FormType, Fields)

    /// <summary>
    /// A form of this type with the given fields; the FORM_TYPE field comes
    /// into being by itself in the process.
    /// </summary>
    public static DiscoForm Of(String                          FormType,
                               params (String Var, String Value)[] Fields)

        => new([
               new DiscoField(DiscoField.FormTypeVar, DiscoField.HiddenType, [FormType]),
               .. Fields.Select(f => new DiscoField(f.Var, null, [f.Value]))
           ]);

    #endregion

    #region (static) SoftwareInfo(...)

    /// <summary>
    /// The <c>softwareinfo</c> form from XEP-0232 - the usual content of
    /// extended information.
    /// </summary>
    /// <remarks>
    /// Entries that are null stay away. A field without a value would not be
    /// the same as a missing one: it would go into the verification string and
    /// would make the hash differ from that of an entity which gives the same
    /// information.
    /// </remarks>
    public static DiscoForm SoftwareInfo(String?  Software          = null,
                                         String?  SoftwareVersion   = null,
                                         String?  OperatingSystem   = null,
                                         String?  OSVersion         = null)
    {

        var fields = new List<(String, String)>(4);

        if (Software        is not null) fields.Add(("software",         Software));
        if (SoftwareVersion is not null) fields.Add(("software_version", SoftwareVersion));
        if (OperatingSystem is not null) fields.Add(("os",               OperatingSystem));
        if (OSVersion       is not null) fields.Add(("os_version",       OSVersion));

        return Of("urn:xmpp:dataforms:softwareinfo", [.. fields]);

    }

    #endregion

}
