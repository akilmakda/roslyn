﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Structure;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Structure;
using Microsoft.CodeAnalysis.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.Editor.CSharp.UnitTests.Structure;

[Trait(Traits.Feature, Traits.Features.Outlining)]
public sealed class StringLiteralExpressionStructureTests : AbstractCSharpSyntaxNodeStructureTests<LiteralExpressionSyntax>
{
    internal override AbstractSyntaxStructureProvider CreateProvider()
        => new StringLiteralExpressionStructureProvider();

    [Fact]
    public async Task TestMultiLineStringLiteral()
    {
        await VerifyBlockSpansAsync(
            """
                class C
                {
                    void M()
                    {
                        var v =
                {|hint:{|textspan:$$@"
                class 
                {
                }
                "|}|};
                    }
                }
                """,
            Region("textspan", "hint", CSharpStructureHelpers.Ellipsis, autoCollapse: true));
    }

    [Fact]
    public async Task TestMissingOnIncompleteStringLiteral()
    {
        await VerifyNoBlockSpansAsync(
            """
                class C
                {
                    void M()
                    {
                        var v = $$";
                    }
                }
                """);
    }
}
