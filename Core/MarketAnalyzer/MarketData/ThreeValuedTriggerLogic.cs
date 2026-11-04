using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Dynamic.Core;
using System.Linq.Expressions;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Three-valued trigger evaluation. Unavailable is neither false nor true:
  /// true OR unavailable is true; false AND unavailable is false.
  /// A final Unavailable result cannot activate a setting or an OffTrigger.
  /// </summary>
  public enum TriggerTruthValue
  {
    False,
    True,
    Unavailable
  }

  public static class ThreeValuedTriggerLogic
  {
    public static TriggerTruthValue And(TriggerTruthValue left, TriggerTruthValue right)
    {
      if (left == TriggerTruthValue.False || right == TriggerTruthValue.False)
      {
        return TriggerTruthValue.False;
      }

      return left == TriggerTruthValue.Unavailable || right == TriggerTruthValue.Unavailable
        ? TriggerTruthValue.Unavailable
        : TriggerTruthValue.True;
    }

    public static TriggerTruthValue Or(TriggerTruthValue left, TriggerTruthValue right)
    {
      if (left == TriggerTruthValue.True || right == TriggerTruthValue.True)
      {
        return TriggerTruthValue.True;
      }

      return left == TriggerTruthValue.Unavailable || right == TriggerTruthValue.Unavailable
        ? TriggerTruthValue.Unavailable
        : TriggerTruthValue.False;
    }

    public static TriggerTruthValue Not(TriggerTruthValue value)
    {
      return value switch
      {
        TriggerTruthValue.True => TriggerTruthValue.False,
        TriggerTruthValue.False => TriggerTruthValue.True,
        _ => TriggerTruthValue.Unavailable
      };
    }

    /// <summary>
    /// Evaluates simple AND/OR connections or a tagged Boolean expression
    /// (for example "(A &amp;&amp; B) || !C"). Existing Dynamic LINQ parsing
    /// is retained, but tags are parsed as variables rather than replacing
    /// arbitrary substrings with Boolean literals.
    /// </summary>
    public static TriggerTruthValue Evaluate(
      string connection,
      IReadOnlyList<TriggerTruthValue> orderedResults,
      IReadOnlyDictionary<string, TriggerTruthValue> taggedResults = null)
    {
      if (String.IsNullOrWhiteSpace(connection))
      {
        throw new ArgumentException("TriggerConnection is missing.", nameof(connection));
      }

      if (orderedResults == null)
      {
        throw new ArgumentNullException(nameof(orderedResults));
      }

      if (connection.Trim().Equals("AND", StringComparison.OrdinalIgnoreCase))
      {
        return orderedResults.Count == 0
          ? TriggerTruthValue.False
          : orderedResults.Aggregate(TriggerTruthValue.True, And);
      }

      if (connection.Trim().Equals("OR", StringComparison.OrdinalIgnoreCase))
      {
        return orderedResults.Count == 0
          ? TriggerTruthValue.False
          : orderedResults.Aggregate(TriggerTruthValue.False, Or);
      }

      if (taggedResults == null)
      {
        throw new ArgumentException("Tagged trigger results are required for custom Boolean expressions.", nameof(taggedResults));
      }

      ParameterExpression[] parameters = taggedResults.Keys.Select(tag =>
      {
        if (String.IsNullOrWhiteSpace(tag))
        {
          throw new ArgumentException("All triggers in a custom Boolean expression must have a nonempty Tag.", nameof(taggedResults));
        }
        return Expression.Parameter(typeof(bool), tag);
      }).ToArray();

      LambdaExpression parsed = DynamicExpressionParser.ParseLambda(
        ParsingConfig.Default, parameters, typeof(bool), connection);

      return EvaluateExpression(parsed.Body, taggedResults);
    }

    private static TriggerTruthValue EvaluateExpression(
      Expression expression,
      IReadOnlyDictionary<string, TriggerTruthValue> tags)
    {
      switch (expression.NodeType)
      {
        case ExpressionType.Constant:
          if (((ConstantExpression)expression).Value is bool constant)
          {
            return constant ? TriggerTruthValue.True : TriggerTruthValue.False;
          }
          break;

        case ExpressionType.Parameter:
          string tag = ((ParameterExpression)expression).Name;
          if (tag != null && tags.TryGetValue(tag, out TriggerTruthValue value))
          {
            return value;
          }
          break;

        case ExpressionType.Not:
          return Not(EvaluateExpression(((UnaryExpression)expression).Operand, tags));

        case ExpressionType.Convert:
        case ExpressionType.ConvertChecked:
          return EvaluateExpression(((UnaryExpression)expression).Operand, tags);

        case ExpressionType.AndAlso:
        case ExpressionType.And:
          BinaryExpression andExpression = (BinaryExpression)expression;
          return And(EvaluateExpression(andExpression.Left, tags), EvaluateExpression(andExpression.Right, tags));

        case ExpressionType.OrElse:
        case ExpressionType.Or:
          BinaryExpression orExpression = (BinaryExpression)expression;
          return Or(EvaluateExpression(orExpression.Left, tags), EvaluateExpression(orExpression.Right, tags));

        case ExpressionType.ExclusiveOr:
          BinaryExpression xorExpression = (BinaryExpression)expression;
          TriggerTruthValue xorLeft = EvaluateExpression(xorExpression.Left, tags);
          TriggerTruthValue xorRight = EvaluateExpression(xorExpression.Right, tags);
          return xorLeft == TriggerTruthValue.Unavailable || xorRight == TriggerTruthValue.Unavailable
            ? TriggerTruthValue.Unavailable
            : xorLeft == xorRight ? TriggerTruthValue.False : TriggerTruthValue.True;

        case ExpressionType.Equal:
        case ExpressionType.NotEqual:
          BinaryExpression equality = (BinaryExpression)expression;
          TriggerTruthValue left = EvaluateExpression(equality.Left, tags);
          TriggerTruthValue right = EvaluateExpression(equality.Right, tags);
          if (left == TriggerTruthValue.Unavailable || right == TriggerTruthValue.Unavailable)
          {
            return TriggerTruthValue.Unavailable;
          }
          bool equal = left == right;
          bool result = expression.NodeType == ExpressionType.Equal ? equal : !equal;
          return result ? TriggerTruthValue.True : TriggerTruthValue.False;

        case ExpressionType.Conditional:
          ConditionalExpression conditional = (ConditionalExpression)expression;
          TriggerTruthValue condition = EvaluateExpression(conditional.Test, tags);
          if (condition == TriggerTruthValue.True)
          {
            return EvaluateExpression(conditional.IfTrue, tags);
          }
          if (condition == TriggerTruthValue.False)
          {
            return EvaluateExpression(conditional.IfFalse, tags);
          }
          TriggerTruthValue whenTrue = EvaluateExpression(conditional.IfTrue, tags);
          TriggerTruthValue whenFalse = EvaluateExpression(conditional.IfFalse, tags);
          return whenTrue == whenFalse ? whenTrue : TriggerTruthValue.Unavailable;
      }

      // Avoid compiling arbitrary expression-tree nodes or converting unknown
      // values to false, especially inside negated custom expressions.
      throw new ArgumentException("Unsupported Boolean TriggerConnection expression node: " + expression.NodeType + ".");
    }
  }
}
