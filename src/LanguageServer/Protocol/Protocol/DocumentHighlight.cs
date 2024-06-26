﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Roslyn.LanguageServer.Protocol
{
    using System.ComponentModel;
    using System.Diagnostics.CodeAnalysis;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Class representing the response from a textDocument/documentHighlight request.
    ///
    /// See the <see href="https://microsoft.github.io/language-server-protocol/specifications/specification-current/#documentHighlight">Language Server Protocol specification</see> for additional information.
    /// </summary>
    internal class DocumentHighlight
    {
        /// <summary>
        /// Gets or sets the range that the highlight applies to.
        /// </summary>
        [JsonPropertyName("range")]
        public Range Range
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the kind of highlight.
        /// </summary>
        [JsonPropertyName("kind")]
        [SuppressMessage("Microsoft.StyleCop.CSharp.LayoutRules", "SA1513:ClosingCurlyBracketMustBeFollowedByBlankLine", Justification = "There are no issues with this code")]
        [SuppressMessage("Microsoft.StyleCop.CSharp.LayoutRules", "SA1500:BracesForMultiLineStatementsShouldNotShareLine", Justification = "There are no issues with this code")]
        [DefaultValue(DocumentHighlightKind.Text)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public DocumentHighlightKind Kind
        {
            get;
            set;
        } = DocumentHighlightKind.Text;
    }
}
