﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis.CodeStyle;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp.Utilities;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.CodeAnalysis.CSharp.UseExpressionBodyForLambda;

internal static class UseExpressionBodyForLambdaHelpers
{
    internal static readonly LocalizableString UseExpressionBodyTitle = new LocalizableResourceString(nameof(CSharpAnalyzersResources.Use_expression_body_for_lambda_expression), CSharpAnalyzersResources.ResourceManager, typeof(CSharpAnalyzersResources));
    internal static readonly LocalizableString UseBlockBodyTitle = new LocalizableResourceString(nameof(CSharpAnalyzersResources.Use_block_body_for_lambda_expression), CSharpAnalyzersResources.ResourceManager, typeof(CSharpAnalyzersResources));

    internal static bool CanOfferUseBlockBody(
        SemanticModel semanticModel, ExpressionBodyPreference preference,
        LambdaExpressionSyntax declaration, CancellationToken cancellationToken)
    {
        var userPrefersBlockBodies = preference == ExpressionBodyPreference.Never;
        if (!userPrefersBlockBodies)
        {
            // If the user doesn't even want block bodies, then certainly do not offer.
            return false;
        }

        var expressionBodyOpt = GetBodyAsExpression(declaration);
        if (expressionBodyOpt == null)
        {
            // they already have a block body.
            return false;
        }

        // We need to know what sort of lambda this is (void returning or not) in order to be
        // able to create the right sort of block body (i.e. with a return-statement or
        // expr-statement).  So, if we can't figure out what lambda type this is, we should not
        // proceed.
        if (semanticModel.GetTypeInfo(declaration, cancellationToken).ConvertedType is not INamedTypeSymbol lambdaType || lambdaType.DelegateInvokeMethod == null)
        {
            return false;
        }

        var canOffer = expressionBodyOpt.TryConvertToStatement(
            semicolonTokenOpt: null, createReturnStatementForExpression: false, out _);
        if (!canOffer)
        {
            // Couldn't even convert the expression into statement form.
            return false;
        }

        var languageVersion = declaration.SyntaxTree.Options.LanguageVersion();
        if (expressionBodyOpt.IsKind(SyntaxKind.ThrowExpression) &&
            languageVersion < LanguageVersion.CSharp7)
        {
            // Can't convert this prior to C# 7 because ```a => throw ...``` isn't allowed.
            return false;
        }

        return true;
    }

    internal static bool CanOfferUseExpressionBody(
        SemanticModel semanticModel, ExpressionBodyPreference preference, LambdaExpressionSyntax declaration, LanguageVersion languageVersion, CancellationToken cancellationToken)
    {
        var userPrefersExpressionBodies = preference != ExpressionBodyPreference.Never;
        if (!userPrefersExpressionBodies)
        {
            // If the user doesn't even want expression bodies, then certainly do not offer.
            return false;
        }

        var expressionBody = GetBodyAsExpression(declaration);
        if (expressionBody != null)
        {
            // they already have an expression body.  so nothing to do here.
            return false;
        }

        // They don't have an expression body.  See if we could convert the block they 
        // have into one.
        return TryConvertToExpressionBody(semanticModel, declaration, languageVersion, preference, cancellationToken, out _);
    }

    internal static ExpressionSyntax? GetBodyAsExpression(LambdaExpressionSyntax declaration)
        => declaration.Body as ExpressionSyntax;

    internal static CodeStyleOption2<ExpressionBodyPreference> GetCodeStyleOption(AnalyzerOptionsProvider provider)
        => ((CSharpAnalyzerOptionsProvider)provider).PreferExpressionBodiedLambdas;

    /// <summary>
    /// Helper to get the true ReportDiagnostic severity for a given option.  Importantly, this
    /// handle ReportDiagnostic.Default and will map that back to the appropriate value in that
    /// case.
    /// </summary>
    internal static ReportDiagnostic GetOptionSeverity(CodeStyleOption2<ExpressionBodyPreference> optionValue)
    {
        var severity = optionValue.Notification.Severity;
        return severity == ReportDiagnostic.Default
            ? severity.WithDefaultSeverity(DiagnosticSeverity.Hidden)
            : severity;
    }

    internal static bool TryConvertToExpressionBody(
        SemanticModel semanticModel,
        LambdaExpressionSyntax declaration,
        LanguageVersion languageVersion,
        ExpressionBodyPreference conversionPreference,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out ExpressionSyntax? expression)
    {
        var body = declaration.Body as BlockSyntax;

        if (!body.TryConvertToExpressionBody(languageVersion, conversionPreference, cancellationToken, out expression, out var semicolonToken))
            return false;

        // If we have directives, we have something like:
        //
        // X(c =>
        // {
        // #if DEBUG
        //      Y();
        // #else
        //      Z();
        // #endif
        // });
        //
        // Converting this to an expression body is a little too complex for us to support currently. We'd have to grab
        // out the parts of the #else/#elif blocks, grab out their expressions, and rewrite into a form like so:
        //
        // X(c =>
        // #if DEBUG
        //      Y()
        // #else
        //      Z()
        // #endif
        // );
        if (semicolonToken.TrailingTrivia.Any(t => t.IsDirective))
            return false;

        // Changing from a block to an expression body can change semantics.  Consider:
        //
        //     X(() => { A = 1; });
        //     
        //     void X(Action action);
        //     void X(Func<int> func);
        //
        // Changing this to `X(() => A = 1);` would change from calling the 'Action' overload to the 'Func<int>'
        // overload.  Do a final semantic check to make sure the code meaning stays the same.
        var speculationAnalyzer = new SpeculationAnalyzer(
            declaration,
            declaration.WithBody(expression),
            semanticModel, cancellationToken);
        if (speculationAnalyzer.ReplacementChangesSemantics())
            return false;

        return true;
    }
}
