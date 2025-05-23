﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Comgenie.Utils
{
    /// <summary>
    /// This class extracts a simplefied filter from an IQueryable expression. 
    /// This can be used to optimize retrieving items from an external storage system.
    /// All items left over will be evaluated using the regular Linq-to-objects method.
    /// </summary>
    /// <typeparam name="T">Element type of the items to query</typeparam>
    public class QueryTranslator<T> : IQueryProvider, IOrderedQueryable<T> 
    {
        //public Func<string, string, bool, int, int, IEnumerable<T>> RetrieveFilteredItems { get; set; } = null; // (filter, orderBy, orderByDesc, take, skip) 
        public Func<Expression, IEnumerable<T>>? FullFilteredItems { get; set; } = null; // (filter) 
        public Func<string, IEnumerable<T>>? SimpleFilteredItems { get; set; } = null; // (filter) 

        /// IQueryable
        public Type ElementType => typeof(T);

        public Expression Expression { get; }

        public IQueryProvider Provider { get; }

        public QueryTranslator(Func<Expression, IEnumerable<T>> fullFilterHandler) : this()
        {
            FullFilteredItems = fullFilterHandler;
        }
        public QueryTranslator(Func<string, IEnumerable<T>> simpleFilterHandler) : this()
        {
            SimpleFilteredItems = simpleFilterHandler; 
        }
        public QueryTranslator()
        {
            this.Expression = Expression.Constant(this);
            this.Provider = this;
        }
        public QueryTranslator(Expression expression)
        {
            this.Expression = expression;
            this.Provider = this;
        }

        public IEnumerator<T> GetEnumerator()
        {
            // https://stackoverflow.com/questions/11785076/custom-iqueryprovider-that-falls-back-on-linqtoobjects

            // We will first let our custom provider do any optimalizations possible when retrieving items
            // Not all expressions are supported and more items may be returned than we need
            var items = this.Execute<IEnumerable<T>>(this.Expression);
            if (items == null)
                items = new List<T>();

            // Copy the items to a list, and apply the same expression to the list
            var itemsList = new List<T>(items);
            var queryable = itemsList.AsQueryable<T>();

            var mc = (MethodCallExpression)this.Expression;
            var exp = Expression.Call(mc.Method, Expression.Constant(queryable), mc.Arguments[1]);

            var query = queryable.Provider.CreateQuery<T>(exp);
            return query.GetEnumerator();

            //return Provider.Execute<IEnumerable<T>>(Expression).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return this.GetEnumerator();
        }


        /// Query provider
        public IQueryable CreateQuery(Expression expression)
        {
            var qb = new QueryTranslator<T>(expression)
            {
                FullFilteredItems = FullFilteredItems,
                SimpleFilteredItems = SimpleFilteredItems
            };
            return qb;
            //return (IQueryable)Activator.CreateInstance(queryType.MakeGenericType(elementType), new object[] { this, expression });
        }

        public IQueryable<TElement> CreateQuery<TElement>(Expression expression) 
        {
            if (typeof(T) != typeof(TElement))
                throw new ArgumentException(nameof(T));

            dynamic qb = new QueryTranslator<TElement>(expression);
            qb.FullFilteredItems = FullFilteredItems;
            qb.SimpleFilteredItems = SimpleFilteredItems;
            return qb;
        }

        public object? Execute(Expression expression)
        {
            return Execute<T>(expression);
        }

        public TResult Execute<TResult>(Expression expression)
        {
            var translator = new SQLQueryTranslator();
            var evaluatedExpression = Evaluator.PartialEval(expression);
            var simple = evaluatedExpression != null ? translator.Translate(evaluatedExpression) : "";

            // Apply filter to internal list
            IEnumerable<T> newRoot;
            if (FullFilteredItems != null)
                newRoot = FullFilteredItems(expression);
            else if (SimpleFilteredItems != null)
                newRoot = SimpleFilteredItems(simple);
            else
                newRoot = new List<T>();

            // Return the rest 
            var isEnumerable = (typeof(TResult).IsGenericType && typeof(TResult).GetGenericTypeDefinition() == typeof(IEnumerable<>));
            if (isEnumerable)
            {
                return (TResult)newRoot.AsEnumerable<T>();
            }
            var resultObj = newRoot.FirstOrDefault();
            
            return (TResult?)((object?)resultObj); // TODO, Check if we can return a non-null value
        }
    }

    // https://stackoverflow.com/questions/7731905/how-to-convert-an-expression-tree-to-a-partial-sql-query
    internal class SQLQueryTranslator : ExpressionVisitor
    {
        private StringBuilder TranslatedQuery;
        private string _orderBy = string.Empty;
        private int? _skip = null;
        private int? _take = null;
        private string _whereClause = string.Empty;

        public int? Skip
        {
            get
            {
                return _skip;
            }
        }

        public int? Take
        {
            get
            {
                return _take;
            }
        }

        public string OrderBy
        {
            get
            {
                return _orderBy;
            }
        }

        public string WhereClause
        {
            get
            {
                return _whereClause;
            }
        }

        public SQLQueryTranslator()
        {
            TranslatedQuery = new StringBuilder();
        }

        public string Translate(Expression expression)
        {
            TranslatedQuery = new StringBuilder();
            this.Visit(expression);
            _whereClause = this.TranslatedQuery.ToString();
            return _whereClause;
        }

        private static Expression StripQuotes(Expression e)
        {
            while (e.NodeType == ExpressionType.Quote)
            {
                e = ((UnaryExpression)e).Operand;
            }
            return e;
        }

        protected override Expression VisitMethodCall(MethodCallExpression m)
        {
            if (m.Method.DeclaringType == typeof(Queryable) && m.Method.Name == "Where")
            {
                this.Visit(m.Arguments[0]);
                LambdaExpression lambda = (LambdaExpression)StripQuotes(m.Arguments[1]);
                this.Visit(lambda.Body);
                return m;
            }
            else if (m.Method.Name == "Take")
            {
                if (this.ParseTakeExpression(m))
                {
                    Expression nextExpression = m.Arguments[0];
                    return this.Visit(nextExpression);
                }
            }
            else if (m.Method.Name == "Skip")
            {
                if (this.ParseSkipExpression(m))
                {
                    Expression nextExpression = m.Arguments[0];
                    return this.Visit(nextExpression);
                }
            }
            else if (m.Method.Name == "OrderBy")
            {
                if (this.ParseOrderByExpression(m, "ASC"))
                {
                    Expression nextExpression = m.Arguments[0];
                    return this.Visit(nextExpression);
                }
            }
            else if (m.Method.Name == "OrderByDescending")
            {
                if (this.ParseOrderByExpression(m, "DESC"))
                {
                    Expression nextExpression = m.Arguments[0];
                    return this.Visit(nextExpression);
                }
            }

            throw new NotSupportedException(string.Format("The method '{0}' is not supported", m.Method.Name));
        }

        protected override Expression VisitUnary(UnaryExpression u)
        {
            switch (u.NodeType)
            {
                case ExpressionType.Not:
                    TranslatedQuery.Append(" NOT ");
                    this.Visit(u.Operand);
                    break;
                case ExpressionType.Convert:
                    this.Visit(u.Operand);
                    break;
                default:
                    throw new NotSupportedException(string.Format("The unary operator '{0}' is not supported", u.NodeType));
            }
            return u;
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="b"></param>
        /// <returns></returns>
        protected override Expression VisitBinary(BinaryExpression b)
        {
            TranslatedQuery.Append("(");
            this.Visit(b.Left);

            switch (b.NodeType)
            {
                case ExpressionType.And:
                    TranslatedQuery.Append(" AND ");
                    break;

                case ExpressionType.AndAlso:
                    TranslatedQuery.Append(" AND ");
                    break;

                case ExpressionType.Or:
                    TranslatedQuery.Append(" OR ");
                    break;

                case ExpressionType.OrElse:
                    TranslatedQuery.Append(" OR ");
                    break;

                case ExpressionType.Equal:
                    if (IsNullConstant(b.Right))
                    {
                        TranslatedQuery.Append(" IS ");
                    }
                    else
                    {
                        TranslatedQuery.Append(" = ");
                    }
                    break;

                case ExpressionType.NotEqual:
                    if (IsNullConstant(b.Right))
                    {
                        TranslatedQuery.Append(" IS NOT ");
                    }
                    else
                    {
                        TranslatedQuery.Append(" <> ");
                    }
                    break;

                case ExpressionType.LessThan:
                    TranslatedQuery.Append(" < ");
                    break;

                case ExpressionType.LessThanOrEqual:
                    TranslatedQuery.Append(" <= ");
                    break;

                case ExpressionType.GreaterThan:
                    TranslatedQuery.Append(" > ");
                    break;

                case ExpressionType.GreaterThanOrEqual:
                    TranslatedQuery.Append(" >= ");
                    break;

                default:
                    throw new NotSupportedException(string.Format("The binary operator '{0}' is not supported", b.NodeType));

            }

            this.Visit(b.Right);
            TranslatedQuery.Append(")");
            return b;
        }

        protected override Expression VisitConstant(ConstantExpression c)
        {
            IQueryable? q = c.Value as IQueryable;

            if (q == null && c.Value == null)
            {
                TranslatedQuery.Append("NULL");
            }
            else if (q == null && c.Value != null)
            {
                switch (Type.GetTypeCode(c.Value.GetType()))
                {
                    case TypeCode.Boolean:
                        TranslatedQuery.Append(((bool)c.Value) ? 1 : 0);
                        break;

                    case TypeCode.String:
                        TranslatedQuery.Append("'");
                        TranslatedQuery.Append(c.Value.ToString()!.Replace("'", "''"));
                        TranslatedQuery.Append("'");
                        break;

                    case TypeCode.DateTime:
                        TranslatedQuery.Append("'");
                        var dateTime = (DateTime)c.Value;
                        TranslatedQuery.Append(dateTime.ToString("yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture)); // change to O to include miliseconds
                        TranslatedQuery.Append("'");
                        break;

                    case TypeCode.Object:
                        throw new NotSupportedException(string.Format("The constant for '{0}' is not supported", c.Value));

                    default:
                        TranslatedQuery.Append(c.Value);
                        break;
                }
            }

            return c;
        }

        protected override Expression VisitMember(MemberExpression m)
        {
            if (m.Expression != null && m.Expression.NodeType == ExpressionType.Parameter)
            {
                TranslatedQuery.Append(m.Member.Name);
                return m;
            }

            throw new NotSupportedException(string.Format("The member '{0}' is not supported", m.Member.Name));
        }

        protected bool IsNullConstant(Expression exp)
        {
            return (exp.NodeType == ExpressionType.Constant && ((ConstantExpression)exp).Value == null);
        }

        private bool ParseOrderByExpression(MethodCallExpression expression, string order)
        {
            UnaryExpression unary = (UnaryExpression)expression.Arguments[1];
            LambdaExpression? lambdaExpression = (LambdaExpression)unary.Operand;

            lambdaExpression = (LambdaExpression?)Evaluator.PartialEval(lambdaExpression);
            if (lambdaExpression == null)
                return false;

            MemberExpression? body = lambdaExpression.Body as MemberExpression;
            if (body != null)
            {
                if (string.IsNullOrEmpty(_orderBy))
                {
                    _orderBy = string.Format("{0} {1}", body.Member.Name, order);
                }
                else
                {
                    _orderBy = string.Format("{0}, {1} {2}", _orderBy, body.Member.Name, order);
                }

                return true;
            }

            return false;
        }

        private bool ParseTakeExpression(MethodCallExpression expression)
        {
            ConstantExpression sizeExpression = (ConstantExpression)expression.Arguments[1];

            int size;
            if (sizeExpression.Value != null && int.TryParse(sizeExpression.Value.ToString(), out size))
            {
                _take = size;
                return true;
            }

            return false;
        }

        private bool ParseSkipExpression(MethodCallExpression expression)
        {
            ConstantExpression sizeExpression = (ConstantExpression)expression.Arguments[1];

            int size;
            if (sizeExpression.Value != null && int.TryParse(sizeExpression.Value.ToString(), out size))
            {
                _skip = size;
                return true;
            }

            return false;
        }
    }


    // https://stackoverflow.com/questions/30308124/force-a-net-expression-to-use-current-value
    internal static class Evaluator
    {
        /// <summary>
        /// Performs evaluation and replacement of independent sub-trees
        /// </summary>
        /// <param name="expression">The root of the expression tree.</param>
        /// <param name="fnCanBeEvaluated">A function that decides whether a given expression node can be part of the local function.</param>
        /// <returns>A new tree with sub-trees evaluated and replaced.</returns>
        public static Expression? PartialEval(Expression expression, Func<Expression, bool> fnCanBeEvaluated)
        {
            return new SubtreeEvaluator(new Nominator(fnCanBeEvaluated).Nominate(expression)).Eval(expression);
        }

        /// <summary>
        /// Performs evaluation and replacement of independent sub-trees
        /// </summary>
        /// <param name="expression">The root of the expression tree.</param>
        /// <returns>A new tree with sub-trees evaluated and replaced.</returns>
        public static Expression? PartialEval(Expression expression)
        {
            return PartialEval(expression, Evaluator.CanBeEvaluatedLocally);
        }

        private static bool CanBeEvaluatedLocally(Expression expression)
        {
            return expression.NodeType != ExpressionType.Parameter;
        }

        /// <summary>
        /// Evaluates and replaces sub-trees when first candidate is reached (top-down)
        /// </summary>
        class SubtreeEvaluator : ExpressionVisitor
        {
            HashSet<Expression> candidates;

            internal SubtreeEvaluator(HashSet<Expression> candidates)
            {
                this.candidates = candidates;
            }

            internal Expression? Eval(Expression exp)
            {
                return this.Visit(exp);
            }

            public override Expression? Visit(Expression? exp)
            {
                if (exp == null)
                {
                    return null;
                }
                if (this.candidates.Contains(exp))
                {
                    return this.Evaluate(exp);
                }
                return base.Visit(exp);
            }

            private Expression Evaluate(Expression e)
            {
                if (e.NodeType == ExpressionType.Constant)
                {
                    return e;
                }
                LambdaExpression lambda = Expression.Lambda(e);
                Delegate fn = lambda.Compile();

                var value = fn.DynamicInvoke(null);
                return Expression.Constant(value, e.Type);
            }
        }

        /// <summary>
        /// Performs bottom-up analysis to determine which nodes can possibly
        /// be part of an evaluated sub-tree.
        /// </summary>
        class Nominator : ExpressionVisitor
        {
            Func<Expression, bool> fnCanBeEvaluated;
            HashSet<Expression>? Candidates;
            bool CannotBeEvaluated;

            internal Nominator(Func<Expression, bool> fnCanBeEvaluated)
            {
                this.fnCanBeEvaluated = fnCanBeEvaluated;
            }

            internal HashSet<Expression> Nominate(Expression expression)
            {
                this.Candidates = new HashSet<Expression>();
                this.Visit(expression);
                return this.Candidates;
            }

            public override Expression? Visit(Expression? expression)
            {
                if (expression != null)
                {
                    if (this.Candidates == null)
                        this.Candidates = new HashSet<Expression>();
                    bool saveCannotBeEvaluated = this.CannotBeEvaluated;
                    this.CannotBeEvaluated = false;
                    base.Visit(expression);
                    if (!this.CannotBeEvaluated)
                    {
                        if (this.fnCanBeEvaluated(expression))
                        {
                            this.Candidates.Add(expression);
                        }
                        else
                        {
                            this.CannotBeEvaluated = true;
                        }
                    }
                    this.CannotBeEvaluated |= saveCannotBeEvaluated;
                }
                return expression;
            }
        }
    }



}
