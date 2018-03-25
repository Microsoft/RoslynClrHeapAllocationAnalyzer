﻿using System;
using System.Collections.Immutable;
using System.Linq;
using ClrHeapAllocationAnalyzer.Common;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ClrHeapAllocationAnalyzer {
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ConcatenationAllocationAnalyzer : AllocationAnalyzer {
        protected override SyntaxKind[] Expressions => new[] { SyntaxKind.AddExpression, SyntaxKind.AddAssignmentExpression };

        private static readonly object[] EmptyMessageArgs = { };

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(
                AllocationRules.GetDescriptor(AllocationRules.StringConcatenationAllocationRule.Id),
                AllocationRules.GetDescriptor(AllocationRules.ValueTypeToReferenceTypeInAStringConcatenationRule.Id)
            );

        protected override void AnalyzeNode(SyntaxNodeAnalysisContext context, EnabledRules rules) {
            var node = context.Node;
            var semanticModel = context.SemanticModel;
            Action<Diagnostic> reportDiagnostic = context.ReportDiagnostic;
            var cancellationToken = context.CancellationToken;
            string filePath = node.SyntaxTree.FilePath;
            var binaryExpressions = node.DescendantNodesAndSelf().OfType<BinaryExpressionSyntax>().Reverse(); // need inner most expressions

            int stringConcatenationCount = 0;
            foreach (var binaryExpression in binaryExpressions) {
                if (binaryExpression.Left == null || binaryExpression.Right == null) {
                    continue;
                }

                bool isConstant = semanticModel.GetConstantValue(binaryExpression, cancellationToken).HasValue;
                if (isConstant) {
                    continue;
                }

                var left = semanticModel.GetTypeInfo(binaryExpression.Left, cancellationToken);
                var right = semanticModel.GetTypeInfo(binaryExpression.Right, cancellationToken);

                if (rules.IsEnabled(AllocationRules.ValueTypeToReferenceTypeInAStringConcatenationRule.Id)) {
                    var leftConversion = semanticModel.GetConversion(binaryExpression.Left, cancellationToken);
                    var rightConversion = semanticModel.GetConversion(binaryExpression.Right, cancellationToken);
                    CheckTypeConversion(rules.Get(AllocationRules.ValueTypeToReferenceTypeInAStringConcatenationRule.Id), left, leftConversion, reportDiagnostic, binaryExpression.Left.GetLocation(), filePath);
                    CheckTypeConversion(rules.Get(AllocationRules.ValueTypeToReferenceTypeInAStringConcatenationRule.Id), right, rightConversion, reportDiagnostic, binaryExpression.Right.GetLocation(), filePath);
                }

                // regular string allocation
                if (rules.IsEnabled(AllocationRules.StringConcatenationAllocationRule.Id)) {
                    if (left.Type?.SpecialType == SpecialType.System_String || right.Type?.SpecialType == SpecialType.System_String) {
                        stringConcatenationCount++;
                    }
                }

                if (stringConcatenationCount > 3) {
                    var rule = rules.Get(AllocationRules.StringConcatenationAllocationRule.Id);
                    reportDiagnostic(Diagnostic.Create(rule, node.GetLocation(), EmptyMessageArgs));
                    HeapAllocationAnalyzerEventSource.Logger.StringConcatenationAllocation(filePath);
                }
            }
        }

        private static void CheckTypeConversion(DiagnosticDescriptor rule, TypeInfo typeInfo, Conversion conversionInfo, Action<Diagnostic> reportDiagnostic, Location location, string filePath) {
            bool IsOptimizedValueType(ITypeSymbol type) {
                return type.SpecialType == SpecialType.System_Boolean ||
                       type.SpecialType == SpecialType.System_Char ||
                       type.SpecialType == SpecialType.System_IntPtr ||
                       type.SpecialType == SpecialType.System_UIntPtr;
            }

            if (conversionInfo.IsBoxing && !IsOptimizedValueType(typeInfo.Type)) {
                reportDiagnostic(Diagnostic.Create(rule, location, new[] { typeInfo.Type.ToDisplayString() }));
                HeapAllocationAnalyzerEventSource.Logger.BoxingAllocationInStringConcatenation(filePath);
            }
        }
    }
}