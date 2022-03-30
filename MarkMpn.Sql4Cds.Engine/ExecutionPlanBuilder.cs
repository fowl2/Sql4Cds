﻿using System;
using System.Collections.Generic;
using System.Data.SqlTypes;
using System.Globalization;
using System.IO;
using System.Linq;
using System.ServiceModel;
using System.Text;
using System.Threading.Tasks;
using MarkMpn.Sql4Cds.Engine.ExecutionPlan;
using MarkMpn.Sql4Cds.Engine.Visitors;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using SelectColumn = MarkMpn.Sql4Cds.Engine.ExecutionPlan.SelectColumn;

namespace MarkMpn.Sql4Cds.Engine
{
    class ExecutionPlanBuilder
    {
        private int _colNameCounter;
        private IDictionary<string, DataTypeReference> _parameterTypes;

        public ExecutionPlanBuilder(IAttributeMetadataCache metadata, ITableSizeCache tableSize, IQueryExecutionOptions options)
            : this(new[] { new DataSource { Name = "local", Metadata = metadata, TableSizeCache = tableSize } }, options)
        {
        }

        public ExecutionPlanBuilder(IEnumerable<DataSource> dataSources, IQueryExecutionOptions options)
        {
            DataSources = dataSources.ToDictionary(ds => ds.Name, StringComparer.OrdinalIgnoreCase);
            Options = options;

            if (!DataSources.ContainsKey(Options.PrimaryDataSource))
                throw new ArgumentOutOfRangeException(nameof(options), "Primary data source " + options.PrimaryDataSource + " not found");

            EstimatedPlanOnly = true;
        }

        /// <summary>
        /// The connections that will be used by this conversion
        /// </summary>
        public IDictionary<string, DataSource> DataSources { get; }

        /// <summary>
        /// Indicates how the query will be executed
        /// </summary>
        public IQueryExecutionOptions Options { get; set; }

        /// <summary>
        /// Indicates if only a simplified plan for display purposes is required
        /// </summary>
        public bool EstimatedPlanOnly { get; set; }

        /// <summary>
        /// Builds the execution plans for a SQL command
        /// </summary>
        /// <param name="sql">The SQL command to generate the execution plans for</param>
        /// <param name="parameters">The types of parameters that are available to the SQL command</param>
        /// <param name="useTDSEndpointDirectly">Indicates if the SQL command should be executed directly against the TDS endpoint</param>
        /// <returns></returns>
        public IRootExecutionPlanNode[] Build(string sql, IDictionary<string, DataTypeReference> parameters, out bool useTDSEndpointDirectly)
        {
            // Take a copy of the defined parameters so we can add more while we're building the query without
            // affecting the original collection until the query is actually run
            _parameterTypes = new Dictionary<string, DataTypeReference>(StringComparer.OrdinalIgnoreCase);

            if (parameters != null)
            {
                foreach (var param in parameters)
                    _parameterTypes[param.Key] = param.Value;
            }

            // Add in standard global variables
            _parameterTypes["@@IDENTITY"] = typeof(SqlEntityReference).ToSqlType();
            _parameterTypes["@@ROWCOUNT"] = typeof(SqlInt32).ToSqlType();

            var queries = new List<IRootExecutionPlanNodeInternal>();

            // Parse the SQL DOM
            var dom = new TSql150Parser(Options.QuotedIdentifiers);
            var fragment = dom.Parse(new StringReader(sql), out var errors);

            // Check if there were any parse errors
            if (errors.Count > 0)
                throw new QueryParseException(errors[0]);

            if (TDSEndpoint.CanUseTDSEndpoint(Options, DataSources[Options.PrimaryDataSource].Connection))
            {
                using (var con = TDSEndpoint.Connect(DataSources[Options.PrimaryDataSource].Connection))
                {
                    var tdsEndpointCompatibilityVisitor = new TDSEndpointCompatibilityVisitor(con, DataSources[Options.PrimaryDataSource].Metadata);
                    fragment.Accept(tdsEndpointCompatibilityVisitor);

                    if (tdsEndpointCompatibilityVisitor.IsCompatible)
                    {
                        useTDSEndpointDirectly = true;
                        var sqlNode = new SqlNode
                        {
                            DataSource = Options.PrimaryDataSource,
                            Sql = sql,
                            Index = 0,
                            Length = sql.Length
                        };

                        if (parameters != null)
                        {
                            foreach (var param in parameters.Keys)
                                sqlNode.Parameters.Add(param);
                        }

                        return new IRootExecutionPlanNode[] { sqlNode };
                    }
                }
            }

            useTDSEndpointDirectly = false;

            var script = (TSqlScript)fragment;
            script.Accept(new ReplacePrimaryFunctionsVisitor());
            var optimizer = new ExecutionPlanOptimizer(DataSources, Options, _parameterTypes, !EstimatedPlanOnly);

            // Convert each statement in turn to the appropriate query type
            foreach (var batch in script.Batches)
            {
                foreach (var statement in batch.Statements)
                    ConvertStatement(statement, optimizer, queries);
            }

            // Ensure GOTOs only reference valid labels
            var labels = new HashSet<string>(GetLabels(queries).Select(n => n.Label), StringComparer.OrdinalIgnoreCase);

            foreach (var gotoNode in queries.OfType<GoToNode>())
            {
                if (!labels.Contains(gotoNode.Label))
                    throw new NotSupportedQueryFragmentException($"A GOTO statement references the label '{gotoNode.Label}' but the label has not been declared.");
            }

            if (EstimatedPlanOnly)
            {
                foreach (var node in queries)
                    EstimateRowsOut(node, _parameterTypes);
            }

            return queries.ToArray();
        }

        private void ConvertStatement(TSqlStatement statement, ExecutionPlanOptimizer optimizer, List<IRootExecutionPlanNodeInternal> queries)
        {
            if (statement is BeginEndBlockStatement block)
            {
                foreach (var stmt in block.StatementList.Statements)
                    ConvertStatement(stmt, optimizer, queries);

                return;
            }

            var index = statement.StartOffset;
            var length = statement.ScriptTokenStream[statement.LastTokenIndex].Offset + statement.ScriptTokenStream[statement.LastTokenIndex].Text.Length - index;
            var originalSql = statement.ToSql();

            IRootExecutionPlanNodeInternal[] plans;
            var hints = statement is StatementWithCtesAndXmlNamespaces stmtWithCtes ? stmtWithCtes.OptimizerHints : null;

            if (statement is SelectStatement select)
                plans = new[] { ConvertSelectStatement(select) };
            else if (statement is UpdateStatement update)
                plans = new[] { ConvertUpdateStatement(update) };
            else if (statement is DeleteStatement delete)
                plans = new[] { ConvertDeleteStatement(delete) };
            else if (statement is InsertStatement insert)
                plans = new[] { ConvertInsertStatement(insert) };
            else if (statement is ExecuteAsStatement impersonate)
                plans = new[] { ConvertExecuteAsStatement(impersonate) };
            else if (statement is RevertStatement revert)
                plans = new[] { ConvertRevertStatement(revert) };
            else if (statement is DeclareVariableStatement declare)
                plans = ConvertDeclareVariableStatement(declare);
            else if (statement is SetVariableStatement set)
                plans = new[] { ConvertSetVariableStatement(set) };
            else if (statement is IfStatement ifStmt)
                plans = new[] { ConvertIfStatement(ifStmt, optimizer) };
            else if (statement is WhileStatement whileStmt)
                plans = new[] { ConvertWhileStatement(whileStmt, optimizer) };
            else if (statement is PrintStatement print)
                plans = new[] { ConvertPrintStatement(print, optimizer) };
            else if (statement is LabelStatement label)
                plans = new[] { ConvertLabelStatement(label, queries) };
            else if (statement is GoToStatement gotoStmt)
                plans = new[] { ConvertGoToStatement(gotoStmt) };
            else if (statement is BreakStatement breakStmt)
                plans = new[] { ConvertBreakStatement(breakStmt) };
            else if (statement is ContinueStatement continueStmt)
                plans = new[] { ConvertContinueStatement(continueStmt) };
            else
                throw new NotSupportedQueryFragmentException("Unsupported statement", statement);

            foreach (var plan in plans)
            {
                SetParent(plan);
                var optimized = optimizer.Optimize(plan, hints);

                foreach (var qry in optimized)
                {
                    if (qry.Sql == null)
                    {
                        qry.Sql = originalSql;
                        qry.Index = index;
                        qry.Length = length;
                    }
                }

                queries.AddRange(optimized);
            }
        }

        private IRootExecutionPlanNodeInternal ConvertContinueStatement(ContinueStatement continueStmt)
        {
            return new ContinueBreakNode { Type = ContinueBreakNodeType.Continue };
        }

        private IRootExecutionPlanNodeInternal ConvertBreakStatement(BreakStatement breakStmt)
        {
            return new ContinueBreakNode { Type = ContinueBreakNodeType.Break };
        }

        private IRootExecutionPlanNodeInternal ConvertGoToStatement(GoToStatement gotoStmt)
        {
            return new GoToNode { Label = gotoStmt.LabelName.Value };
        }

        private IRootExecutionPlanNodeInternal ConvertLabelStatement(LabelStatement label, List<IRootExecutionPlanNodeInternal> queries)
        {
            // Check this label hasn't already been defined
            if (GetLabels(queries).Any(l => l.Label.Equals(label.Value, StringComparison.OrdinalIgnoreCase)))
                throw new NotSupportedQueryFragmentException("The label has already been declared. Label names must be unique within a query batch or stored procedure.", label);

            return new GotoLabelNode { Label = label.Value.TrimEnd(':') };
        }

        private IEnumerable<GotoLabelNode> GetLabels(IEnumerable<IRootExecutionPlanNodeInternal> queries)
        {
            return queries.OfType<GotoLabelNode>()
                .Concat(queries.SelectMany(q => GetLabels(q.GetSources().OfType<IRootExecutionPlanNodeInternal>())));
        }

        private IRootExecutionPlanNodeInternal ConvertPrintStatement(PrintStatement print, ExecutionPlanOptimizer optimizer)
        {
            // Check if the value is a simple expression or requires a query. Subqueries are not allowed
            var subqueryVisitor = new ScalarSubqueryVisitor();
            print.Expression.Accept(subqueryVisitor);

            if (subqueryVisitor.Subqueries.Count > 0)
                throw new NotSupportedQueryFragmentException("Subqueries are not allowed in this context. Only scalar expressions are allowed.", print.Expression);

            // Check the expression for errors. Ensure it can be converted to a string
            var expr = print.Expression;

            if (print.Expression.GetType(null, null, _parameterTypes) != typeof(string))
            {
                expr = new ConvertCall
                {
                    DataType = typeof(SqlString).ToSqlType(),
                    Parameter = print.Expression
                };

                expr.GetType(null, null, _parameterTypes);
            }

            return new PrintNode
            {
                Expression = expr
            };
        }

        private IRootExecutionPlanNodeInternal ConvertIfWhileStatement(ConditionalNodeType type, BooleanExpression predicate, TSqlStatement trueStatement, TSqlStatement falseStatement, ExecutionPlanOptimizer optimizer)
        {
            // Check if the predicate is a simple expression or requires a query
            var subqueryVisitor = new ScalarSubqueryVisitor();
            predicate.Accept(subqueryVisitor);
            IDataExecutionPlanNodeInternal predicateSource = null;
            string sourceCol = null;

            if (subqueryVisitor.Subqueries.Count == 0)
            {
                // Check the predicate for errors
                predicate.GetType(null, null, _parameterTypes);
            }
            else
            {
                // Convert predicate to query - IF EXISTS(qry) => SELECT CASE WHEN EXISTS(qry) THEN 1 ELSE 0 END
                var select = new QuerySpecification
                {
                    SelectElements =
                    {
                        new SelectScalarExpression
                        {
                            Expression = new SearchedCaseExpression
                            {
                                WhenClauses =
                                {
                                    new SearchedWhenClause
                                    {
                                        WhenExpression = predicate,
                                        ThenExpression = new IntegerLiteral { Value = "1" }
                                    }
                                },
                                ElseExpression = new IntegerLiteral { Value = "0" }
                            }
                        }
                    }
                };

                var selectQry = ConvertSelectQuerySpec(select, Array.Empty<OptimizerHint>(), null, null, _parameterTypes);
                predicateSource = selectQry.Source;
                sourceCol = selectQry.ColumnSet[0].SourceColumn;
            }

            // Convert the true & false branches
            var trueQueries = new List<IRootExecutionPlanNodeInternal>();
            ConvertStatement(trueStatement, optimizer, trueQueries);

            List<IRootExecutionPlanNodeInternal> falseQueries = null;

            if (falseStatement != null)
            {
                falseQueries = new List<IRootExecutionPlanNodeInternal>();
                ConvertStatement(falseStatement, optimizer, falseQueries);
            }

            return new ConditionalNode
            {
                Condition = subqueryVisitor.Subqueries.Count == 0 ? predicate : null,
                Source = predicateSource,
                SourceColumn = sourceCol,
                TrueStatements = trueQueries.ToArray(),
                FalseStatements = falseQueries?.ToArray(),
                Type = type
            };
        }

        private IRootExecutionPlanNodeInternal ConvertWhileStatement(WhileStatement whileStmt, ExecutionPlanOptimizer optimizer)
        {
            return ConvertIfWhileStatement(ConditionalNodeType.While, whileStmt.Predicate, whileStmt.Statement, null, optimizer);
        }

        private IRootExecutionPlanNodeInternal ConvertIfStatement(IfStatement ifStmt, ExecutionPlanOptimizer optimizer)
        {
            return ConvertIfWhileStatement(ConditionalNodeType.If, ifStmt.Predicate, ifStmt.ThenStatement, ifStmt.ElseStatement, optimizer);
        }

        private IRootExecutionPlanNodeInternal ConvertSetVariableStatement(SetVariableStatement set)
        {
            if (set.CursorDefinition != null)
                throw new NotSupportedQueryFragmentException("Cursors are not supported", set.CursorDefinition);

            if (set.FunctionCallExists)
                throw new NotSupportedQueryFragmentException("Custom functions are not supported", set);

            if (set.Identifier != null)
                throw new NotSupportedQueryFragmentException("User defined types are not supported", set);

            if (set.Parameters != null && set.Parameters.Count > 0)
                throw new NotSupportedQueryFragmentException("Parameters are not supported", set.Parameters[0]);

            if (!_parameterTypes.TryGetValue(set.Variable.Name, out var paramType))
                throw new NotSupportedQueryFragmentException("Must declare the scalar variable", set.Variable);

            // Create the SELECT statement that generates the required information
            var expr = set.Expression;

            switch (set.AssignmentKind)
            {
                case AssignmentKind.AddEquals:
                    expr = new BinaryExpression { FirstExpression = set.Variable, BinaryExpressionType = BinaryExpressionType.Add, SecondExpression = expr };
                    break;

                case AssignmentKind.BitwiseAndEquals:
                    expr = new BinaryExpression { FirstExpression = set.Variable, BinaryExpressionType = BinaryExpressionType.BitwiseAnd, SecondExpression = expr };
                    break;

                case AssignmentKind.BitwiseOrEquals:
                    expr = new BinaryExpression { FirstExpression = set.Variable, BinaryExpressionType = BinaryExpressionType.BitwiseOr, SecondExpression = expr };
                    break;

                case AssignmentKind.BitwiseXorEquals:
                    expr = new BinaryExpression { FirstExpression = set.Variable, BinaryExpressionType = BinaryExpressionType.BitwiseXor, SecondExpression = expr };
                    break;

                case AssignmentKind.DivideEquals:
                    expr = new BinaryExpression { FirstExpression = set.Variable, BinaryExpressionType = BinaryExpressionType.Divide, SecondExpression = expr };
                    break;

                case AssignmentKind.ModEquals:
                    expr = new BinaryExpression { FirstExpression = set.Variable, BinaryExpressionType = BinaryExpressionType.Modulo, SecondExpression = expr };
                    break;

                case AssignmentKind.MultiplyEquals:
                    expr = new BinaryExpression { FirstExpression = set.Variable, BinaryExpressionType = BinaryExpressionType.Multiply, SecondExpression = expr };
                    break;

                case AssignmentKind.SubtractEquals:
                    expr = new BinaryExpression { FirstExpression = set.Variable, BinaryExpressionType = BinaryExpressionType.Subtract, SecondExpression = expr };
                    break;
            }

            expr = new ConvertCall { DataType = paramType, Parameter = expr };
            expr.ScriptTokenStream = null;

            var queryExpression = new QuerySpecification();
            queryExpression.SelectElements.Add(new SelectScalarExpression { Expression = expr, ColumnName = new IdentifierOrValueExpression { Identifier = new Identifier { Value = "Value" } } });
            var selectStatement = new SelectStatement { QueryExpression = queryExpression };

            var source = ConvertSelectStatement(selectStatement);

            var node = new AssignVariablesNode();

            if (source is SelectNode select)
            {
                node.Source = select.Source;
                node.Variables.Add(new VariableAssignment { VariableName = set.Variable.Name, SourceColumn = select.ColumnSet[0].SourceColumn });
            }
            else
            {
                node.Source = source;
                node.Variables.Add(new VariableAssignment { VariableName = set.Variable.Name, SourceColumn = "Value" });
            }

            return node;
        }

        private IRootExecutionPlanNodeInternal[] ConvertDeclareVariableStatement(DeclareVariableStatement declare)
        {
            var nodes = new List<IRootExecutionPlanNodeInternal>();
            var declareNode = new DeclareVariablesNode();
            nodes.Add(declareNode);

            foreach (var declaration in declare.Declarations)
            {
                if (_parameterTypes.ContainsKey(declaration.VariableName.Value))
                    throw new NotSupportedQueryFragmentException("The variable name has already been declared. Variable names must be unique within a query batch", declaration.VariableName);

                // Apply default maximum length for [n][var]char types
                if (declaration.DataType is SqlDataTypeReference dataType)
                {
                    if (dataType.SqlDataTypeOption == SqlDataTypeOption.Cursor)
                        throw new NotSupportedQueryFragmentException("Cursors are not supported", dataType);

                    if (dataType.SqlDataTypeOption == SqlDataTypeOption.Table)
                        throw new NotSupportedQueryFragmentException("Table variables are not supported", dataType);

                    if (dataType.SqlDataTypeOption == SqlDataTypeOption.Char ||
                    dataType.SqlDataTypeOption == SqlDataTypeOption.NChar ||
                    dataType.SqlDataTypeOption == SqlDataTypeOption.VarChar ||
                    dataType.SqlDataTypeOption == SqlDataTypeOption.NVarChar)
                    {
                        if (dataType.Parameters.Count == 0)
                            dataType.Parameters.Add(new IntegerLiteral { Value = "1" });
                    }
                }

                declareNode.Variables[declaration.VariableName.Value] = declaration.DataType;

                // Make the variables available in our local copy of parameters so later statements
                // in the same batch can use them
                _parameterTypes[declaration.VariableName.Value] = declaration.DataType;

                if (declaration.Value != null)
                {
                    var setStatement = new SetVariableStatement
                    {
                        Variable = new VariableReference { Name = declaration.VariableName.Value },
                        AssignmentKind = AssignmentKind.Equals,
                        Expression = declaration.Value
                    };

                    nodes.Add(ConvertSetVariableStatement(setStatement));
                }
            }

            return nodes.ToArray();
        }

        private void SetParent(IExecutionPlanNodeInternal plan)
        {
            foreach (IExecutionPlanNodeInternal child in plan.GetSources())
            {
                child.Parent = plan;
                SetParent(child);
            }
        }

        private ExecuteAsNode ConvertExecuteAsStatement(ExecuteAsStatement impersonate)
        {
            // Check for any DOM elements we don't support converting
            if (impersonate.Cookie != null)
                throw new NotSupportedQueryFragmentException("Unhandled impersonation cookie", impersonate.Cookie);

            if (impersonate.WithNoRevert)
                throw new NotSupportedQueryFragmentException("Unhandled WITH NO REVERT option", impersonate);

            if (impersonate.ExecuteContext.Kind != ExecuteAsOption.Login &&
                impersonate.ExecuteContext.Kind != ExecuteAsOption.User)
                throw new NotSupportedQueryFragmentException("Unhandled impersonation type", impersonate.ExecuteContext);

            if (!(impersonate.ExecuteContext.Principal is StringLiteral user))
                throw new NotSupportedQueryFragmentException("Unhandled username variable", impersonate.ExecuteContext.Principal);

            IExecutionPlanNodeInternal source;

            if (impersonate.ExecuteContext.Kind == ExecuteAsOption.Login)
            {
                // Create a SELECT query to find the user ID
                var selectStatement = new SelectStatement
                {
                    QueryExpression = new QuerySpecification
                    {
                        FromClause = new FromClause
                        {
                            TableReferences =
                            {
                                new NamedTableReference { SchemaObject = new SchemaObjectName { Identifiers = { new Identifier { Value = "systemuser" } } } }
                            }
                        },
                        WhereClause = new WhereClause
                        {
                            SearchCondition = new BooleanComparisonExpression
                            {
                                FirstExpression = "domainname".ToColumnReference(),
                                ComparisonType = BooleanComparisonType.Equals,
                                SecondExpression = user
                            }
                        },
                        SelectElements =
                        {
                            new SelectScalarExpression
                            {
                                Expression = "systemuserid".ToColumnReference()
                            }
                        }
                    }
                };

                var select = ConvertSelectStatement(selectStatement);

                if (select is SelectNode selectNode)
                    source = selectNode.Source;
                else
                    source = select;
            }
            else
            {
                // User ID is provided directly. Check it's a valid guid
                if (!Guid.TryParse(user.Value, out var userId))
                {
                    throw new NotSupportedQueryFragmentException("Invalid user ID", user)
                    {
                        Suggestion = "User GUID must be supplied when using EXECUTE AS USER. To use the user login name (e.g. user@contoso.onmicrosoft.com) to identify the user to impersonate, use EXECUTE AS LOGIN instead"
                    };
                }

                source = new ConstantScanNode
                {
                    Alias = "systemuser",
                    Schema =
                    {
                        ["systemuserid"] = typeof(SqlString).ToSqlType()
                    },
                    Values =
                    {
                        new Dictionary<string, ScalarExpression>
                        {
                            ["systemuserid"] = user
                        }
                    }
                };
            }

            return new ExecuteAsNode
            {
                UserIdSource = "systemuser.systemuserid",
                Source = source,
                DataSource = Options.PrimaryDataSource
            };
        }

        private RevertNode ConvertRevertStatement(RevertStatement revert)
        {
            return new RevertNode
            {
                DataSource = Options.PrimaryDataSource
            };
        }

        /// <summary>
        /// Convert an INSERT statement from SQL
        /// </summary>
        /// <param name="insert">The parsed INSERT statement</param>
        /// <returns>The equivalent query converted for execution against CDS</returns>
        private InsertNode ConvertInsertStatement(InsertStatement insert)
        {
            // Check for any DOM elements that don't have an equivalent in CDS
            if (insert.WithCtesAndXmlNamespaces != null)
                throw new NotSupportedQueryFragmentException("Unhandled INSERT WITH clause", insert.WithCtesAndXmlNamespaces);

            if (insert.InsertSpecification.Columns == null)
                throw new NotSupportedQueryFragmentException("Unhandled INSERT without column specification", insert);

            if (insert.InsertSpecification.OutputClause != null)
                throw new NotSupportedQueryFragmentException("Unhandled INSERT OUTPUT clause", insert.InsertSpecification.OutputClause);

            if (insert.InsertSpecification.OutputIntoClause != null)
                throw new NotSupportedQueryFragmentException("Unhandled INSERT OUTPUT INTO clause", insert.InsertSpecification.OutputIntoClause);

            if (!(insert.InsertSpecification.Target is NamedTableReference target))
                throw new NotSupportedQueryFragmentException("Unhandled INSERT target", insert.InsertSpecification.Target);

            // Check if we are inserting constant values or the results of a SELECT statement and perform the appropriate conversion
            IExecutionPlanNodeInternal source;
            string[] columns;

            if (insert.InsertSpecification.InsertSource is ValuesInsertSource values)
                source = ConvertInsertValuesSource(values, insert.OptimizerHints, null, null, _parameterTypes, out columns);
            else if (insert.InsertSpecification.InsertSource is SelectInsertSource select)
                source = ConvertInsertSelectSource(select, insert.OptimizerHints, out columns);
            else
                throw new NotSupportedQueryFragmentException("Unhandled INSERT source", insert.InsertSpecification.InsertSource);

            return ConvertInsertSpecification(target, insert.InsertSpecification.Columns, source, columns);
        }

        private IDataExecutionPlanNodeInternal ConvertInsertValuesSource(ValuesInsertSource values, IList<OptimizerHint> hints, INodeSchema outerSchema, Dictionary<string, string> outerReferences, IDictionary<string, DataTypeReference> parameterTypes, out string[] columns)
        {
            // Convert the values to an InlineDerviedTable
            var table = new InlineDerivedTable
            {
                Alias = new Identifier { Value = $"Expr{++_colNameCounter}" }
            };

            foreach (var col in values.RowValues[0].ColumnValues)
                table.Columns.Add(new Identifier { Value = $"Expr{++_colNameCounter}" });

            foreach (var row in values.RowValues)
                table.RowValues.Add(row);

            columns = table.Columns.Select(col => col.Value).ToArray();
            return ConvertInlineDerivedTable(table, hints, outerSchema, outerReferences, parameterTypes);
        }

        private IExecutionPlanNodeInternal ConvertInsertSelectSource(SelectInsertSource selectSource, IList<OptimizerHint> hints, out string[] columns)
        {
            var selectStatement = new SelectStatement { QueryExpression = selectSource.Select };
            var select = ConvertSelectStatement(selectStatement);

            if (select is SelectNode selectNode)
            {
                columns = selectNode.ColumnSet.Select(col => col.SourceColumn).ToArray();
                return selectNode.Source;
            }

            if (select is SqlNode sql)
            {
                columns = null;
                return sql;
            }

            throw new NotSupportedQueryFragmentException("Unhandled INSERT source", selectSource);
        }

        private DataSource SelectDataSource(SchemaObjectName schemaObject)
        {
            var databaseName = schemaObject.DatabaseIdentifier?.Value ?? Options.PrimaryDataSource;
            
            if (!DataSources.TryGetValue(databaseName, out var dataSource))
                throw new NotSupportedQueryFragmentException("Invalid database name", schemaObject) { Suggestion = $"Available database names:\r\n* {String.Join("\r\n*", DataSources.Keys.OrderBy(k => k))}" };

            return dataSource;
        }

        private InsertNode ConvertInsertSpecification(NamedTableReference target, IList<ColumnReferenceExpression> targetColumns, IExecutionPlanNodeInternal source, string[] sourceColumns)
        {
            var dataSource = SelectDataSource(target.SchemaObject);

            var node = new InsertNode
            {
                DataSource = dataSource.Name,
                LogicalName = target.SchemaObject.BaseIdentifier.Value,
                Source = source
            };

            if (!String.IsNullOrEmpty(target.SchemaObject.SchemaIdentifier?.Value) &&
                !target.SchemaObject.SchemaIdentifier.Value.Equals("dbo", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedQueryFragmentException("Invalid schema name", target.SchemaObject.SchemaIdentifier) { Suggestion = "All data tables are in the 'dbo' schema" };

            // Validate the entity name
            EntityMetadata metadata;

            try
            {
                metadata = dataSource.Metadata[node.LogicalName];
            }
            catch (FaultException ex)
            {
                throw new NotSupportedQueryFragmentException(ex.Message, target);
            }

            var attributes = metadata.Attributes.ToDictionary(attr => attr.LogicalName, StringComparer.OrdinalIgnoreCase);
            var attributeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var virtualTypeAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var schema = sourceColumns == null ? null : ((IDataExecutionPlanNodeInternal)source).GetSchema(DataSources, _parameterTypes);

            // Check all target columns are valid for create
            foreach (var col in targetColumns)
            {
                var colName = col.GetColumnName();

                // Could be a virtual ___type attribute where the "real" virtual attribute uses a different name, e.g.
                // entityid in listmember has an associated entitytypecode attribute
                if (colName.EndsWith("type", StringComparison.OrdinalIgnoreCase) &&
                    attributes.TryGetValue(colName.Substring(0, colName.Length - 4), out var attr) &&
                    attr is LookupAttributeMetadata lookupAttr &&
                    lookupAttr.Targets.Length > 1)
                {
                    if (!virtualTypeAttributes.Add(colName))
                        throw new NotSupportedQueryFragmentException("Duplicate column name", col);

                    continue;
                }

                if (!attributes.TryGetValue(colName, out attr))
                    throw new NotSupportedQueryFragmentException("Unknown column", col);

                if (!attributeNames.Add(colName))
                    throw new NotSupportedQueryFragmentException("Duplicate column name", col);

                if (metadata.LogicalName == "listmember")
                {
                    if (attr.LogicalName != "listid" && attr.LogicalName != "entityid")
                        throw new NotSupportedQueryFragmentException("Only the listid and entityid columns can be used when inserting values into the listmember table", col);
                }
                else if (metadata.IsIntersect == true)
                {
                    var relationship = metadata.ManyToManyRelationships.Single();

                    if (attr.LogicalName != relationship.Entity1IntersectAttribute && attr.LogicalName != relationship.Entity2IntersectAttribute)
                        throw new NotSupportedQueryFragmentException($"Only the {relationship.Entity1IntersectAttribute} and {relationship.Entity2IntersectAttribute} columns can be used when inserting values into the {metadata.LogicalName} table", col);
                }
                else
                {
                    if (attr.IsValidForCreate == false)
                        throw new NotSupportedQueryFragmentException("Column is not valid for INSERT", col);
                }
            }

            // Special case: inserting into listmember requires listid and entityid
            if (metadata.LogicalName == "listmember")
            {
                if (!attributeNames.Contains("listid"))
                    throw new NotSupportedQueryFragmentException("Inserting values into the listmember table requires the listid column to be set", target);
                if (!attributeNames.Contains("entityid"))
                    throw new NotSupportedQueryFragmentException("Inserting values into the listmember table requires the entity column to be set", target);
            }
            else if (metadata.IsIntersect == true)
            {
                var relationship = metadata.ManyToManyRelationships.Single();
                if (!attributeNames.Contains(relationship.Entity1IntersectAttribute))
                    throw new NotSupportedQueryFragmentException($"Inserting values into the {metadata.LogicalName} table requires the {relationship.Entity1IntersectAttribute} column to be set", target);
                if (!attributeNames.Contains(relationship.Entity2IntersectAttribute))
                    throw new NotSupportedQueryFragmentException($"Inserting values into the {metadata.LogicalName} table requires the {relationship.Entity2IntersectAttribute} column to be set", target);
            }

            if (sourceColumns == null)
            {
                // Source is TDS endpoint so can't validate the columns, assume they are correct
                for (var i = 0; i < targetColumns.Count; i++)
                    node.ColumnMappings[targetColumns[i].GetColumnName()] = i.ToString();
            }
            else
            {
                if (targetColumns.Count != sourceColumns.Length)
                    throw new NotSupportedQueryFragmentException("Column number mismatch");

                for (var i = 0; i < targetColumns.Count; i++)
                {
                    string targetName;
                    Type targetType;

                    var colName = targetColumns[i].GetColumnName();
                    if (virtualTypeAttributes.Contains(colName))
                    {
                        targetName = colName;
                        targetType = typeof(SqlString);
                    }
                    else
                    {
                        var attr = attributes[colName];
                        targetName = attr.LogicalName;
                        targetType = attr.GetAttributeSqlType();

                        // If we're inserting into a lookup field, the field type will be a SqlEntityReference. Change this to
                        // a SqlGuid so we can accept any guid values, including from TDS endpoint where SqlEntityReference
                        // values will not be available
                        if (targetType == typeof(SqlEntityReference))
                            targetType = typeof(SqlGuid);
                    }

                    if (!schema.ContainsColumn(sourceColumns[i], out var sourceColumn))
                        throw new NotSupportedQueryFragmentException("Invalid source column");

                    var sourceType = schema.Schema[sourceColumn].ToNetType(out _);

                    if (!SqlTypeConverter.CanChangeTypeImplicit(sourceType, targetType))
                        throw new NotSupportedQueryFragmentException($"No implicit type conversion from {sourceType} to {targetType}", targetColumns[i]);

                    node.ColumnMappings[targetName] = sourceColumn;
                }
            }

            // If any of the insert columns are a polymorphic lookup field, make sure we've got a value for the associated type field too
            foreach (var col in targetColumns)
            {
                var targetAttrName = col.GetColumnName();

                if (attributeNames.Contains(targetAttrName))
                {
                    var targetLookupAttribute = attributes[targetAttrName] as LookupAttributeMetadata;

                    if (targetLookupAttribute == null)
                        continue;

                    if (targetLookupAttribute.Targets.Length > 1 &&
                        !virtualTypeAttributes.Contains(targetAttrName + "type") &&
                        targetLookupAttribute.AttributeType != AttributeTypeCode.PartyList &&
                        (schema == null || node.ColumnMappings[targetAttrName].ToColumnReference().GetType(schema, null, null) != typeof(SqlEntityReference)))
                    {
                        // Special case: not required for listmember.entityid
                        if (metadata.LogicalName == "listmember" && targetLookupAttribute.LogicalName == "entityid")
                            continue;

                        throw new NotSupportedQueryFragmentException("Inserting values into a polymorphic lookup field requires setting the associated type column as well", col)
                        {
                            Suggestion = $"Add a value for the {targetLookupAttribute.LogicalName}type column and set it to one of the following values:\r\n{String.Join("\r\n", targetLookupAttribute.Targets.Select(t => $"* {t}"))}"
                        };
                    }
                }
                else if (virtualTypeAttributes.Contains(targetAttrName))
                {
                    var idAttrName = targetAttrName.Substring(0, targetAttrName.Length - 4);

                    if (!attributeNames.Contains(idAttrName))
                    {
                        throw new NotSupportedQueryFragmentException("Inserting values into a polymorphic type field requires setting the associated ID column as well", col)
                        {
                            Suggestion = $"Add a value for the {idAttrName} column"
                        };
                    }
                }
            }

            return node;
        }

        private DeleteNode ConvertDeleteStatement(DeleteStatement delete)
        {
            if (delete.WithCtesAndXmlNamespaces != null)
                throw new NotSupportedQueryFragmentException("Unsupported CTE clause", delete.WithCtesAndXmlNamespaces);

            return ConvertDeleteStatement(delete.DeleteSpecification, delete.OptimizerHints);
        }

        private DeleteNode ConvertDeleteStatement(DeleteSpecification delete, IList<OptimizerHint> hints)
        {
            if (delete.OutputClause != null)
                throw new NotSupportedQueryFragmentException("Unsupported OUTPUT clause", delete.OutputClause);

            if (delete.OutputIntoClause != null)
                throw new NotSupportedQueryFragmentException("Unsupported OUTPUT INTO clause", delete.OutputIntoClause);

            if (!(delete.Target is NamedTableReference target))
                throw new NotSupportedQueryFragmentException("Unsupported DELETE target", delete.Target);

            if (delete.WhereClause == null && Options.BlockDeleteWithoutWhere)
            {
                throw new NotSupportedQueryFragmentException("DELETE without WHERE is blocked by your settings", delete)
                {
                    Suggestion = "Add a WHERE clause to limit the records that will be deleted, or disable the \"Prevent DELETE without WHERE\" option in the settings window"
                };
            }

            if (!String.IsNullOrEmpty(target.SchemaObject.SchemaIdentifier?.Value) &&
                !target.SchemaObject.SchemaIdentifier.Value.Equals("dbo", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedQueryFragmentException("Invalid schema name", target.SchemaObject.SchemaIdentifier) { Suggestion = "All data tables are in the 'dbo' schema" };

            // Create the SELECT statement that generates the required information
            var queryExpression = new QuerySpecification
            {
                FromClause = delete.FromClause ?? new FromClause { TableReferences = { target } },
                WhereClause = delete.WhereClause,
                UniqueRowFilter = UniqueRowFilter.Distinct,
                TopRowFilter = delete.TopRowFilter
            };

            var deleteTarget = new UpdateTargetVisitor(target.SchemaObject, Options.PrimaryDataSource);
            queryExpression.FromClause.Accept(deleteTarget);

            if (String.IsNullOrEmpty(deleteTarget.TargetEntityName))
                throw new NotSupportedQueryFragmentException("Target table not found in FROM clause", target);

            if (deleteTarget.Ambiguous)
                throw new NotSupportedQueryFragmentException("Target table name is ambiguous", target);

            if (!DataSources.TryGetValue(deleteTarget.TargetDataSource, out var dataSource))
                throw new NotSupportedQueryFragmentException("Invalid database name", target.SchemaObject.DatabaseIdentifier) { Suggestion = $"Available database names:\r\n* {String.Join("\r\n*", DataSources.Keys.OrderBy(k => k))}" };

            var targetAlias = deleteTarget.TargetAliasName ?? deleteTarget.TargetEntityName;
            var targetLogicalName = deleteTarget.TargetEntityName;

            EntityMetadata targetMetadata;

            try
            {
                targetMetadata = dataSource.Metadata[targetLogicalName];
            }
            catch (FaultException ex)
            {
                throw new NotSupportedQueryFragmentException(ex.Message, deleteTarget.Target);
            }

            var primaryKey = targetMetadata.PrimaryIdAttribute;
            string secondaryKey = null;

            if (targetMetadata.LogicalName == "listmember")
            {
                primaryKey = "listid";
                secondaryKey = "entityid";
            }
            else if (targetMetadata.IsIntersect == true)
            {
                var relationship = targetMetadata.ManyToManyRelationships.Single();
                primaryKey = relationship.Entity1IntersectAttribute;
                secondaryKey = relationship.Entity2IntersectAttribute;
            }
            
            queryExpression.SelectElements.Add(new SelectScalarExpression
            {
                Expression = new ColumnReferenceExpression
                {
                    MultiPartIdentifier = new MultiPartIdentifier
                    {
                        Identifiers =
                        {
                            new Identifier { Value = targetAlias },
                            new Identifier { Value = primaryKey }
                        }
                    }
                },
                ColumnName = new IdentifierOrValueExpression
                {
                    Identifier = new Identifier { Value = primaryKey }
                }
            });

            if (secondaryKey != null)
            {
                queryExpression.SelectElements.Add(new SelectScalarExpression
                {
                    Expression = new ColumnReferenceExpression
                    {
                        MultiPartIdentifier = new MultiPartIdentifier
                        {
                            Identifiers =
                        {
                            new Identifier { Value = targetAlias },
                            new Identifier { Value = secondaryKey }
                        }
                        }
                    },
                    ColumnName = new IdentifierOrValueExpression
                    {
                        Identifier = new Identifier { Value = secondaryKey }
                    }
                });
            }
            
            var selectStatement = new SelectStatement { QueryExpression = queryExpression };

            foreach (var hint in hints)
                selectStatement.OptimizerHints.Add(hint);

            var source = ConvertSelectStatement(selectStatement);

            // Add DELETE
            var deleteNode = new DeleteNode
            {
                LogicalName = targetMetadata.LogicalName,
                DataSource = dataSource.Name
            };

            if (source is SelectNode select)
            {
                deleteNode.Source = select.Source;
                deleteNode.PrimaryIdSource = $"{targetAlias}.{primaryKey}";

                if (secondaryKey != null)
                    deleteNode.SecondaryIdSource = $"{targetAlias}.{secondaryKey}";
            }
            else
            {
                deleteNode.Source = source;
                deleteNode.PrimaryIdSource = primaryKey;
                deleteNode.SecondaryIdSource = secondaryKey;
            }

            return deleteNode;
        }

        private UpdateNode ConvertUpdateStatement(UpdateStatement update)
        {
            if (update.WithCtesAndXmlNamespaces != null)
                throw new NotSupportedQueryFragmentException("Unsupported CTE clause", update.WithCtesAndXmlNamespaces);

            return ConvertUpdateStatement(update.UpdateSpecification, update.OptimizerHints);
        }

        private UpdateNode ConvertUpdateStatement(UpdateSpecification update, IList<OptimizerHint> hints)
        {
            if (update.OutputClause != null)
                throw new NotSupportedQueryFragmentException("Unsupported OUTPUT clause", update.OutputClause);

            if (update.OutputIntoClause != null)
                throw new NotSupportedQueryFragmentException("Unsupported OUTPUT INTO clause", update.OutputIntoClause);

            if (!(update.Target is NamedTableReference target))
                throw new NotSupportedQueryFragmentException("Unsupported UPDATE target", update.Target);

            if (update.WhereClause == null && Options.BlockUpdateWithoutWhere)
            {
                throw new NotSupportedQueryFragmentException("UPDATE without WHERE is blocked by your settings", update)
                {
                    Suggestion = "Add a WHERE clause to limit the records that will be affected by the update, or disable the \"Prevent UPDATE without WHERE\" option in the settings window"
                };
            }

            if (!String.IsNullOrEmpty(target.SchemaObject.SchemaIdentifier?.Value) &&
                !target.SchemaObject.SchemaIdentifier.Value.Equals("dbo", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedQueryFragmentException("Invalid schema name", target.SchemaObject.SchemaIdentifier) { Suggestion = "All data tables are in the 'dbo' schema" };

            // Create the SELECT statement that generates the required information
            var queryExpression = new QuerySpecification
            {
                FromClause = update.FromClause ?? new FromClause { TableReferences = { target } },
                WhereClause = update.WhereClause,
                UniqueRowFilter = UniqueRowFilter.Distinct,
                TopRowFilter = update.TopRowFilter
            };

            var updateTarget = new UpdateTargetVisitor(target.SchemaObject, Options.PrimaryDataSource);
            queryExpression.FromClause.Accept(updateTarget);

            if (String.IsNullOrEmpty(updateTarget.TargetEntityName))
                throw new NotSupportedQueryFragmentException("Target table not found in FROM clause", target);

            if (updateTarget.Ambiguous)
                throw new NotSupportedQueryFragmentException("Target table name is ambiguous", target);

            if (!DataSources.TryGetValue(updateTarget.TargetDataSource, out var dataSource))
                throw new NotSupportedQueryFragmentException("Invalid database name", target.SchemaObject.DatabaseIdentifier) { Suggestion = $"Available database names:\r\n* {String.Join("\r\n*", DataSources.Keys.OrderBy(k => k))}" };

            var targetAlias = updateTarget.TargetAliasName ?? updateTarget.TargetEntityName;
            var targetLogicalName = updateTarget.TargetEntityName;

            EntityMetadata targetMetadata;

            try
            {
                targetMetadata = dataSource.Metadata[targetLogicalName];
            }
            catch (FaultException ex)
            {
                throw new NotSupportedQueryFragmentException(ex.Message, updateTarget.Target);
            }

            if (targetMetadata.IsIntersect == true)
            {
                throw new NotSupportedQueryFragmentException("Cannot update many-to-many intersect entities", updateTarget.Target)
                {
                    Suggestion = "DELETE any unwanted records and then INSERT the correct values instead"
                };
            }

            queryExpression.SelectElements.Add(new SelectScalarExpression
            {
                Expression = new ColumnReferenceExpression
                {
                    MultiPartIdentifier = new MultiPartIdentifier
                    {
                        Identifiers =
                        {
                            new Identifier { Value = targetAlias },
                            new Identifier { Value = targetMetadata.PrimaryIdAttribute }
                        }
                    }
                },
                ColumnName = new IdentifierOrValueExpression
                {
                    Identifier = new Identifier { Value = targetMetadata.PrimaryIdAttribute }
                }
            });

            var attributes = targetMetadata.Attributes.ToDictionary(attr => attr.LogicalName, StringComparer.OrdinalIgnoreCase);
            var attributeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var virtualTypeAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var set in update.SetClauses)
            {
                if (!(set is AssignmentSetClause assignment))
                    throw new NotSupportedQueryFragmentException("Unhandled SET clause", set);

                if (assignment.Variable != null)
                    throw new NotSupportedQueryFragmentException("Unhandled variable SET clause", set);

                switch (assignment.AssignmentKind)
                {
                    case AssignmentKind.AddEquals:
                        assignment.NewValue = new BinaryExpression { FirstExpression = assignment.Column, BinaryExpressionType = BinaryExpressionType.Add, SecondExpression = assignment.NewValue };
                        break;

                    case AssignmentKind.BitwiseAndEquals:
                        assignment.NewValue = new BinaryExpression { FirstExpression = assignment.Column, BinaryExpressionType = BinaryExpressionType.BitwiseAnd, SecondExpression = assignment.NewValue };
                        break;

                    case AssignmentKind.BitwiseOrEquals:
                        assignment.NewValue = new BinaryExpression { FirstExpression = assignment.Column, BinaryExpressionType = BinaryExpressionType.BitwiseOr, SecondExpression = assignment.NewValue };
                        break;

                    case AssignmentKind.BitwiseXorEquals:
                        assignment.NewValue = new BinaryExpression { FirstExpression = assignment.Column, BinaryExpressionType = BinaryExpressionType.BitwiseXor, SecondExpression = assignment.NewValue };
                        break;

                    case AssignmentKind.DivideEquals:
                        assignment.NewValue = new BinaryExpression { FirstExpression = assignment.Column, BinaryExpressionType = BinaryExpressionType.Divide, SecondExpression = assignment.NewValue };
                        break;

                    case AssignmentKind.ModEquals:
                        assignment.NewValue = new BinaryExpression { FirstExpression = assignment.Column, BinaryExpressionType = BinaryExpressionType.Modulo, SecondExpression = assignment.NewValue };
                        break;

                    case AssignmentKind.MultiplyEquals:
                        assignment.NewValue = new BinaryExpression { FirstExpression = assignment.Column, BinaryExpressionType = BinaryExpressionType.Multiply, SecondExpression = assignment.NewValue };
                        break;

                    case AssignmentKind.SubtractEquals:
                        assignment.NewValue = new BinaryExpression { FirstExpression = assignment.Column, BinaryExpressionType = BinaryExpressionType.Subtract, SecondExpression = assignment.NewValue };
                        break;
                }

                // Validate the target attribute
                var targetAttrName = assignment.Column.MultiPartIdentifier.Identifiers.Last().Value.ToLower();

                // Could be a virtual ___type attribute where the "real" virtual attribute uses a different name, e.g.
                // entityid in listmember has an associated entitytypecode attribute
                if (targetAttrName.EndsWith("type", StringComparison.OrdinalIgnoreCase) &&
                    attributes.TryGetValue(targetAttrName.Substring(0, targetAttrName.Length - 4), out var attr) &&
                    attr is LookupAttributeMetadata lookupAttr &&
                    lookupAttr.Targets.Length > 1)
                {
                    if (!virtualTypeAttributes.Add(targetAttrName))
                        throw new NotSupportedQueryFragmentException("Duplicate column name", assignment.Column);
                }
                else
                {
                    if (!attributes.TryGetValue(targetAttrName, out attr))
                        throw new NotSupportedQueryFragmentException("Unknown column name", assignment.Column);

                    if (attr.IsValidForUpdate == false)
                        throw new NotSupportedQueryFragmentException("Column cannot be updated", assignment.Column);

                    if (!attributeNames.Add(attr.LogicalName))
                        throw new NotSupportedQueryFragmentException("Duplicate column name", assignment.Column);

                    targetAttrName = attr.LogicalName;
                }

                queryExpression.SelectElements.Add(new SelectScalarExpression { Expression = assignment.NewValue, ColumnName = new IdentifierOrValueExpression { Identifier = new Identifier { Value = targetAttrName } } });
            }

            var selectStatement = new SelectStatement { QueryExpression = queryExpression };

            foreach (var hint in hints)
                selectStatement.OptimizerHints.Add(hint);

            var source = ConvertSelectStatement(selectStatement);

            // Add UPDATE
            var updateNode = ConvertSetClause(update.SetClauses, dataSource, source, targetLogicalName, targetAlias, attributeNames, virtualTypeAttributes);

            return updateNode;
        }

        private UpdateNode ConvertSetClause(IList<SetClause> setClauses, DataSource dataSource, IExecutionPlanNodeInternal node, string targetLogicalName, string targetAlias, HashSet<string> attributeNames, HashSet<string> virtualTypeAttributes)
        {
            var targetMetadata = dataSource.Metadata[targetLogicalName];
            var attributes = targetMetadata.Attributes.ToDictionary(attr => attr.LogicalName, StringComparer.OrdinalIgnoreCase);
            var sourceTypes = new Dictionary<string, Type>();

            var update = new UpdateNode
            {
                LogicalName = targetMetadata.LogicalName,
                DataSource = dataSource.Name
            };

            if (node is SelectNode select)
            {
                update.Source = select.Source;
                update.PrimaryIdSource = $"{targetAlias}.{targetMetadata.PrimaryIdAttribute}";

                var schema = select.Source.GetSchema(DataSources, null);

                foreach (var assignment in setClauses.Cast<AssignmentSetClause>())
                {
                    // Validate the type conversion
                    var targetAttrName = assignment.Column.MultiPartIdentifier.Identifiers.Last().Value;
                    Type targetType;

                    // Could be a virtual ___type attribute where the "real" virtual attribute uses a different name, e.g.
                    // entityid in listmember has an associated entitytypecode attribute
                    if (virtualTypeAttributes.Contains(targetAttrName))
                    {
                        targetType = typeof(SqlString);
                    }
                    else
                    {
                        var targetAttribute = attributes[targetAttrName];
                        targetType = targetAttribute.GetAttributeSqlType();

                        // If we're updating a lookup field, the field type will be a SqlEntityReference. Change this to
                        // a SqlGuid so we can accept any guid values, including from TDS endpoint where SqlEntityReference
                        // values will not be available
                        if (targetType == typeof(SqlEntityReference))
                            targetType = typeof(SqlGuid);
                    }

                    var sourceColName = select.ColumnSet.Single(col => col.OutputColumn == targetAttrName.ToLower()).SourceColumn;
                    var sourceCol = sourceColName.ToColumnReference();
                    var sourceType = sourceCol.GetType(schema, null, null);

                    if (!SqlTypeConverter.CanChangeTypeImplicit(sourceType, targetType))
                        throw new NotSupportedQueryFragmentException($"Cannot convert value of type {sourceType} to {targetType}", assignment);

                    if (update.ColumnMappings.ContainsKey(targetAttrName))
                        throw new NotSupportedQueryFragmentException("Duplicate target column", assignment.Column);

                    sourceTypes[targetAttrName] = sourceType;

                    // Normalize the column name
                    schema.ContainsColumn(sourceColName, out sourceColName);
                    update.ColumnMappings[targetAttrName] = sourceColName;
                }
            }
            else
            {
                update.Source = node;
                update.PrimaryIdSource = targetMetadata.PrimaryIdAttribute;

                foreach (var assignment in setClauses.Cast<AssignmentSetClause>())
                {
                    var targetAttrName = assignment.Column.MultiPartIdentifier.Identifiers.Last().Value;
                    update.ColumnMappings[targetAttrName] = targetAttrName;
                }
            }

            // If any of the updates are for a polymorphic lookup field, make sure we've got an update for the associated type field too
            foreach (var assignment in setClauses.Cast<AssignmentSetClause>())
            {
                var targetAttrName = assignment.Column.MultiPartIdentifier.Identifiers.Last().Value;

                if (attributeNames.Contains(targetAttrName))
                {
                    var targetLookupAttribute = attributes[targetAttrName] as LookupAttributeMetadata;

                    if (targetLookupAttribute == null)
                        continue;

                    if (targetLookupAttribute.Targets.Length > 1 &&
                        !virtualTypeAttributes.Contains(targetAttrName + "type") &&
                        targetLookupAttribute.AttributeType != AttributeTypeCode.PartyList &&
                        (!sourceTypes.TryGetValue(targetAttrName, out var sourceType) || sourceType != typeof(SqlEntityReference)))
                    {
                        throw new NotSupportedQueryFragmentException("Updating a polymorphic lookup field requires setting the associated type column as well", assignment.Column)
                        {
                            Suggestion = $"Add a SET clause for the {targetLookupAttribute.LogicalName}type column and set it to one of the following values:\r\n{String.Join("\r\n", targetLookupAttribute.Targets.Select(t => $"* {t}"))}"
                        };
                    }
                }
                else if (virtualTypeAttributes.Contains(targetAttrName))
                {
                    var idAttrName = targetAttrName.Substring(0, targetAttrName.Length - 4);

                    if (!attributeNames.Contains(idAttrName))
                    {
                        throw new NotSupportedQueryFragmentException("Updating a polymorphic type field requires setting the associated ID column as well", assignment.Column)
                        {
                            Suggestion = $"Add a SET clause for the {idAttrName} column"
                        };
                    }
                }
            }

            return update;
        }

        private IRootExecutionPlanNodeInternal ConvertSelectStatement(SelectStatement select)
        {
            if (TDSEndpoint.CanUseTDSEndpoint(Options, DataSources[Options.PrimaryDataSource].Connection))
            {
                using (var con = TDSEndpoint.Connect(DataSources[Options.PrimaryDataSource].Connection))
                {
                    var tdsEndpointCompatibilityVisitor = new TDSEndpointCompatibilityVisitor(con, DataSources[Options.PrimaryDataSource].Metadata, false);
                    select.Accept(tdsEndpointCompatibilityVisitor);

                    if (tdsEndpointCompatibilityVisitor.IsCompatible)
                    {
                        select.ScriptTokenStream = null;
                        var sql = new SqlNode
                        {
                            DataSource = Options.PrimaryDataSource,
                            Sql = select.ToSql()
                        };

                        var variables = new VariableCollectingVisitor();
                        select.Accept(variables);

                        foreach (var variable in variables.Variables)
                            sql.Parameters.Add(variable.Name);

                        return sql;
                    }
                }
            }

            if (select.ComputeClauses != null && select.ComputeClauses.Count > 0)
                throw new NotSupportedQueryFragmentException("Unsupported COMPUTE clause", select.ComputeClauses[0]);

            if (select.Into != null)
                throw new NotSupportedQueryFragmentException("Unsupported INTO clause", select.Into);

            if (select.On != null)
                throw new NotSupportedQueryFragmentException("Unsupported ON clause", select.On);

            if (select.WithCtesAndXmlNamespaces != null)
                throw new NotSupportedQueryFragmentException("Unsupported CTE clause", select.WithCtesAndXmlNamespaces);

            var variableAssignments = new List<string>();
            SelectElement firstNonSetSelectElement = null;

            if (select.QueryExpression is QuerySpecification querySpec)
            {
                for (var i = 0; i < querySpec.SelectElements.Count; i++)
                {
                    var selectElement = querySpec.SelectElements[i];

                    if (selectElement is SelectSetVariable set)
                    {
                        if (firstNonSetSelectElement != null)
                            throw new NotSupportedQueryFragmentException("A SELECT statement that assigns a value to a variable must not be combined with data-retrieval operations", selectElement);

                        variableAssignments.Add(set.Variable.Name);

                        if (!_parameterTypes.TryGetValue(set.Variable.Name, out var paramType))
                            throw new NotSupportedQueryFragmentException("Must declare the scalar variable", set.Variable);

                        // Create the SELECT statement that generates the required information
                        var expr = set.Expression;

                        switch (set.AssignmentKind)
                        {
                            case AssignmentKind.AddEquals:
                                expr = new BinaryExpression { FirstExpression = set.Variable, BinaryExpressionType = BinaryExpressionType.Add, SecondExpression = expr };
                                break;

                            case AssignmentKind.BitwiseAndEquals:
                                expr = new BinaryExpression { FirstExpression = set.Variable, BinaryExpressionType = BinaryExpressionType.BitwiseAnd, SecondExpression = expr };
                                break;

                            case AssignmentKind.BitwiseOrEquals:
                                expr = new BinaryExpression { FirstExpression = set.Variable, BinaryExpressionType = BinaryExpressionType.BitwiseOr, SecondExpression = expr };
                                break;

                            case AssignmentKind.BitwiseXorEquals:
                                expr = new BinaryExpression { FirstExpression = set.Variable, BinaryExpressionType = BinaryExpressionType.BitwiseXor, SecondExpression = expr };
                                break;

                            case AssignmentKind.DivideEquals:
                                expr = new BinaryExpression { FirstExpression = set.Variable, BinaryExpressionType = BinaryExpressionType.Divide, SecondExpression = expr };
                                break;

                            case AssignmentKind.ModEquals:
                                expr = new BinaryExpression { FirstExpression = set.Variable, BinaryExpressionType = BinaryExpressionType.Modulo, SecondExpression = expr };
                                break;

                            case AssignmentKind.MultiplyEquals:
                                expr = new BinaryExpression { FirstExpression = set.Variable, BinaryExpressionType = BinaryExpressionType.Multiply, SecondExpression = expr };
                                break;

                            case AssignmentKind.SubtractEquals:
                                expr = new BinaryExpression { FirstExpression = set.Variable, BinaryExpressionType = BinaryExpressionType.Subtract, SecondExpression = expr };
                                break;
                        }

                        expr = new ConvertCall { DataType = paramType, Parameter = expr };
                        expr.ScriptTokenStream = null;

                        querySpec.SelectElements[i] = new SelectScalarExpression { Expression = expr };
                    }
                    else if (firstNonSetSelectElement == null)
                    {
                        if (variableAssignments.Count > 0)
                            throw new NotSupportedQueryFragmentException("A SELECT statement that assigns a value to a variable must not be combined with data-retrieval operations", selectElement);

                        firstNonSetSelectElement = selectElement;
                    }
                }
            }

            var converted = ConvertSelectStatement(select.QueryExpression, select.OptimizerHints, null, null, _parameterTypes);

            if (variableAssignments.Count > 0)
            {
                var assign = new AssignVariablesNode
                {
                    Source = converted.Source
                };

                for (var i = 0; i < variableAssignments.Count; i++)
                    assign.Variables.Add(new VariableAssignment { SourceColumn = converted.ColumnSet[i].SourceColumn, VariableName = variableAssignments[i] });

                return assign;
            }

            return converted;
        }

        private SelectNode ConvertSelectStatement(QueryExpression query, IList<OptimizerHint> hints, INodeSchema outerSchema, Dictionary<string,string> outerReferences, IDictionary<string, DataTypeReference> parameterTypes)
        {
            if (query is QuerySpecification querySpec)
                return ConvertSelectQuerySpec(querySpec, hints, outerSchema, outerReferences, parameterTypes);

            if (query is BinaryQueryExpression binary)
                return ConvertBinaryQuery(binary, hints, outerSchema, outerReferences, parameterTypes);

            if (query is QueryParenthesisExpression paren)
            {
                paren.QueryExpression.ForClause = paren.ForClause;
                paren.QueryExpression.OffsetClause = paren.OffsetClause;
                paren.QueryExpression.OrderByClause = paren.OrderByClause;
                return ConvertSelectStatement(paren.QueryExpression, hints, outerSchema, outerReferences, parameterTypes);
            }

            throw new NotSupportedQueryFragmentException("Unhandled SELECT query expression", query);
        }

        private SelectNode ConvertBinaryQuery(BinaryQueryExpression binary, IList<OptimizerHint> hints, INodeSchema outerSchema, Dictionary<string, string> outerReferences, IDictionary<string, DataTypeReference> parameterTypes)
        {
            if (binary.BinaryQueryExpressionType != BinaryQueryExpressionType.Union)
                throw new NotSupportedQueryFragmentException($"Unhandled {binary.BinaryQueryExpressionType} query type", binary);

            if (binary.ForClause != null)
                throw new NotSupportedQueryFragmentException("Unhandled FOR clause", binary.ForClause);

            var left = ConvertSelectStatement(binary.FirstQueryExpression, hints, outerSchema, outerReferences, parameterTypes);
            var right = ConvertSelectStatement(binary.SecondQueryExpression, hints, outerSchema, outerReferences, parameterTypes);

            var concat = left.Source as ConcatenateNode;

            if (concat == null)
            {
                concat = new ConcatenateNode();

                concat.Sources.Add(left.Source);

                foreach (var col in left.ColumnSet)
                {
                    concat.ColumnSet.Add(new ConcatenateColumn
                    {
                        OutputColumn = $"Expr{++_colNameCounter}",
                        SourceColumns = { col.SourceColumn },
                        SourceExpressions = { col.SourceExpression }
                    });
                }
            }

            concat.Sources.Add(right.Source);

            if (concat.ColumnSet.Count != right.ColumnSet.Count)
                throw new NotSupportedQueryFragmentException("UNION must have the same number of columns in each query", binary);

            for (var i = 0; i < concat.ColumnSet.Count; i++)
            {
                concat.ColumnSet[i].SourceColumns.Add(right.ColumnSet[i].SourceColumn);
                concat.ColumnSet[i].SourceExpressions.Add(right.ColumnSet[i].SourceExpression);
            }

            var node = (IDataExecutionPlanNodeInternal)concat;

            if (!binary.All)
            {
                var distinct = new DistinctNode { Source = node };
                distinct.Columns.AddRange(concat.ColumnSet.Select(col => col.OutputColumn));
                node = distinct;
            }

            node = ConvertOrderByClause(node, hints, binary.OrderByClause, concat.ColumnSet.Select(col => new ColumnReferenceExpression { MultiPartIdentifier = new MultiPartIdentifier { Identifiers = { new Identifier { Value = col.OutputColumn } } } }).ToArray(), binary, parameterTypes, outerSchema, outerReferences, null);
            node = ConvertOffsetClause(node, binary.OffsetClause, parameterTypes);

            var select = new SelectNode { Source = node };
            select.ColumnSet.AddRange(concat.ColumnSet.Select((col, i) => new SelectColumn { SourceColumn = col.OutputColumn, SourceExpression = col.SourceExpressions[0], OutputColumn = left.ColumnSet[i].OutputColumn }));

            return select;
        }

        private SelectNode ConvertSelectQuerySpec(QuerySpecification querySpec, IList<OptimizerHint> hints, INodeSchema outerSchema, Dictionary<string,string> outerReferences, IDictionary<string, DataTypeReference> parameterTypes)
        {
            // Check for any aggregates in the FROM or WHERE clauses
            var aggregateCollector = new AggregateCollectingVisitor();
            if (querySpec.FromClause != null)
            {
                querySpec.FromClause.Accept(aggregateCollector);

                if (aggregateCollector.Aggregates.Any())
                    throw new NotSupportedQueryFragmentException("An aggregate may not appear in the FROM clause", aggregateCollector.Aggregates[0]);
            }
            if (querySpec.WhereClause != null)
            {
                querySpec.WhereClause.Accept(aggregateCollector);

                if (aggregateCollector.Aggregates.Any())
                    throw new NotSupportedQueryFragmentException("An aggregate may not appear in the WHERE clause", aggregateCollector.Aggregates[0]);
            }

            // Each table in the FROM clause starts as a separate FetchXmlScan node. Add appropriate join nodes
            var node = querySpec.FromClause == null ? new ConstantScanNode { Values = { new Dictionary<string, ScalarExpression>() } } : ConvertFromClause(querySpec.FromClause.TableReferences, hints, querySpec, outerSchema, outerReferences, parameterTypes);

            node = ConvertInSubqueries(node, hints, querySpec, parameterTypes, outerSchema, outerReferences);
            node = ConvertExistsSubqueries(node, hints, querySpec, parameterTypes, outerSchema, outerReferences);

            // Add filters from WHERE
            node = ConvertWhereClause(node, hints, querySpec.WhereClause, outerSchema, outerReferences, parameterTypes, querySpec);

            // Add aggregates from GROUP BY/SELECT/HAVING/ORDER BY
            var preGroupByNode = node;
            node = ConvertGroupByAggregates(node, querySpec, parameterTypes, outerSchema, outerReferences);
            var nonAggregateSchema = preGroupByNode == node ? null : preGroupByNode.GetSchema(DataSources, parameterTypes);

            // Add filters from HAVING
            node = ConvertHavingClause(node, hints, querySpec.HavingClause, parameterTypes, outerSchema, outerReferences, querySpec, nonAggregateSchema);

            // Add sorts from ORDER BY
            var selectFields = new List<ScalarExpression>();
            var preOrderSchema = node.GetSchema(DataSources, parameterTypes);
            foreach (var el in querySpec.SelectElements)
            {
                if (el is SelectScalarExpression expr)
                {
                    selectFields.Add(expr.Expression);
                }
                else if (el is SelectStarExpression star)
                {
                    foreach (var field in preOrderSchema.Schema.Keys.OrderBy(f => f))
                    {
                        if (star.Qualifier == null || field.StartsWith(String.Join(".", star.Qualifier.Identifiers.Select(id => id.Value)) + "."))
                        {
                            var colRef = new ColumnReferenceExpression { MultiPartIdentifier = new MultiPartIdentifier() };
                            foreach (var part in field.Split('.'))
                                colRef.MultiPartIdentifier.Identifiers.Add(new Identifier { Value = part });

                            selectFields.Add(colRef);
                        }
                    }
                }
            }

            node = ConvertOrderByClause(node, hints, querySpec.OrderByClause, selectFields.ToArray(), querySpec, parameterTypes, outerSchema, outerReferences, nonAggregateSchema);

            // Add DISTINCT
            var distinct = querySpec.UniqueRowFilter == UniqueRowFilter.Distinct ? new DistinctNode { Source = node } : null;
            node = distinct ?? node;

            // Add TOP/OFFSET
            if (querySpec.TopRowFilter != null && querySpec.OffsetClause != null)
                throw new NotSupportedQueryFragmentException("A TOP can not be used in the same query or sub-query as a OFFSET.", querySpec.TopRowFilter);

            node = ConvertTopClause(node, querySpec.TopRowFilter, querySpec.OrderByClause, parameterTypes);
            node = ConvertOffsetClause(node, querySpec.OffsetClause, parameterTypes);

            // Add SELECT
            var selectNode = ConvertSelectClause(querySpec.SelectElements, hints, node, distinct, querySpec, parameterTypes, outerSchema, outerReferences, nonAggregateSchema);

            return selectNode;
        }

        private IDataExecutionPlanNodeInternal ConvertInSubqueries(IDataExecutionPlanNodeInternal source, IList<OptimizerHint> hints, TSqlFragment query, IDictionary<string, DataTypeReference> parameterTypes, INodeSchema outerSchema, IDictionary<string,string> outerReferences)
        {
            var visitor = new InSubqueryVisitor();
            query.Accept(visitor);

            if (visitor.InSubqueries.Count == 0)
                return source;

            var computeScalar = source as ComputeScalarNode;
            var rewrites = new Dictionary<BooleanExpression, BooleanExpression>();
            var schema = source.GetSchema(DataSources, parameterTypes);

            foreach (var inSubquery in visitor.InSubqueries)
            {
                // Validate the LHS expression
                inSubquery.Expression.GetType(schema, null, parameterTypes);

                // Each query of the format "col1 IN (SELECT col2 FROM source)" becomes a left outer join:
                // LEFT JOIN source ON col1 = col2
                // and the result is col2 IS NOT NULL

                // Ensure the left hand side is a column
                if (!(inSubquery.Expression is ColumnReferenceExpression lhsCol))
                {
                    if (computeScalar == null)
                    {
                        computeScalar = new ComputeScalarNode { Source = source };
                        source = computeScalar;
                    }

                    var alias = $"Expr{++_colNameCounter}";
                    computeScalar.Columns[alias] = inSubquery.Expression;
                    lhsCol = alias.ToColumnReference();
                }
                else
                {
                    // Normalize the LHS column
                    if (schema.ContainsColumn(lhsCol.GetColumnName(), out var lhsColNormalized))
                        lhsCol = lhsColNormalized.ToColumnReference();
                }

                var parameters = parameterTypes == null ? new Dictionary<string, DataTypeReference>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, DataTypeReference>(parameterTypes, StringComparer.OrdinalIgnoreCase);
                var references = new Dictionary<string, string>();
                var innerQuery = ConvertSelectStatement(inSubquery.Subquery.QueryExpression, hints, schema, references, parameters);

                // Scalar subquery must return exactly one column and one row
                if (innerQuery.ColumnSet.Count != 1)
                    throw new NotSupportedQueryFragmentException("IN subquery must return exactly one column", inSubquery.Subquery);

                // Create the join
                BaseJoinNode join;
                var testColumn = innerQuery.ColumnSet[0].SourceColumn;

                if (references.Count == 0)
                {
                    if (UseMergeJoin(source, innerQuery.Source, references, testColumn, lhsCol.GetColumnName(), true, out var outputCol, out var merge))
                    {
                        testColumn = outputCol;
                        join = merge;
                    }
                    else
                    {
                        // We need the inner list to be distinct to avoid creating duplicates during the join
                        var innerSchema = innerQuery.Source.GetSchema(DataSources, parameters);
                        if (innerQuery.ColumnSet[0].SourceColumn != innerSchema.PrimaryKey && !(innerQuery.Source is DistinctNode))
                        {
                            innerQuery.Source = new DistinctNode
                            {
                                Source = innerQuery.Source,
                                Columns = { innerQuery.ColumnSet[0].SourceColumn }
                            };
                        }

                        // This isn't a correlated subquery, so we can use a foldable join type. Alias the results so there's no conflict with the
                        // same table being used inside the IN subquery and elsewhere
                        var alias = new AliasNode(innerQuery, new Identifier { Value = $"Expr{++_colNameCounter}" });

                        testColumn = $"{alias.Alias}.{alias.ColumnSet[0].OutputColumn}";
                        join = new HashJoinNode
                        {
                            LeftSource = source,
                            LeftAttribute = lhsCol,
                            RightSource = alias,
                            RightAttribute = new ColumnReferenceExpression { MultiPartIdentifier = new MultiPartIdentifier { Identifiers = { new Identifier { Value = alias.Alias }, new Identifier { Value = alias.ColumnSet[0].OutputColumn } } } }
                        };
                    }

                    if (!join.SemiJoin)
                    {
                        // Convert the join to a semi join to ensure requests for wildcard columns aren't folded to the IN subquery
                        var definedValue = $"Expr{++_colNameCounter}";
                        join.SemiJoin = true;
                        join.DefinedValues[definedValue] = testColumn;
                        testColumn = definedValue;
                    }
                }
                else
                {
                    // We need to use nested loops for correlated subqueries
                    // TODO: We could use a hash join where there is a simple correlation, but followed by a distinct node to eliminate duplicates
                    // We could also move the correlation criteria out of the subquery and into the join condition. We would then make one request to
                    // get all the related records and spool that in memory to get the relevant results in the nested loop. Need to understand how 
                    // many rows are likely from the outer query to work out if this is going to be more efficient or not.
                    if (innerQuery.Source is ISingleSourceExecutionPlanNode loopRightSourceSimple)
                        InsertCorrelatedSubquerySpool(loopRightSourceSimple, source, hints, parameterTypes, references.Values.ToArray());

                    var definedValue = $"Expr{++_colNameCounter}";

                    join = new NestedLoopNode
                    {
                        LeftSource = source,
                        RightSource = innerQuery.Source,
                        OuterReferences = references,
                        JoinCondition = new BooleanComparisonExpression
                        {
                            FirstExpression = lhsCol,
                            ComparisonType = BooleanComparisonType.Equals,
                            SecondExpression = innerQuery.ColumnSet[0].SourceColumn.ToColumnReference()
                        },
                        SemiJoin = true,
                        DefinedValues = { [definedValue] = innerQuery.ColumnSet[0].SourceColumn }
                    };

                    testColumn = definedValue;
                }

                join.JoinType = QualifiedJoinType.LeftOuter;

                rewrites[inSubquery] = new BooleanIsNullExpression
                {
                    IsNot = !inSubquery.NotDefined,
                    Expression = testColumn.ToColumnReference()
                };

                source = join;
            }

            query.Accept(new BooleanRewriteVisitor(rewrites));

            return source;
        }

        private IDataExecutionPlanNodeInternal ConvertExistsSubqueries(IDataExecutionPlanNodeInternal source, IList<OptimizerHint> hints, TSqlFragment query, IDictionary<string, DataTypeReference> parameterTypes, INodeSchema outerSchema, IDictionary<string, string> outerReferences)
        {
            var visitor = new ExistsSubqueryVisitor();
            query.Accept(visitor);

            if (visitor.ExistsSubqueries.Count == 0)
                return source;

            var rewrites = new Dictionary<BooleanExpression, BooleanExpression>();
            var schema = source.GetSchema(DataSources, parameterTypes);

            foreach (var existsSubquery in visitor.ExistsSubqueries)
            {
                // Each query of the format "EXISTS (SELECT * FROM source)" becomes a outer semi join
                var parameters = parameterTypes == null ? new Dictionary<string, DataTypeReference>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, DataTypeReference>(parameterTypes, StringComparer.OrdinalIgnoreCase);
                var references = new Dictionary<string, string>();
                var innerQuery = ConvertSelectStatement(existsSubquery.Subquery.QueryExpression, hints, schema, references, parameters);
                var innerSchema = innerQuery.Source.GetSchema(DataSources, parameters);
                var innerSchemaPrimaryKey = innerSchema.PrimaryKey;

                // Create the join
                BaseJoinNode join;
                string testColumn;
                if (references.Count == 0)
                {
                    // We only need one record to check for EXISTS
                    if (!(innerQuery.Source is TopNode) && !(innerQuery.Source is OffsetFetchNode))
                    {
                        innerQuery.Source = new TopNode
                        {
                            Source = innerQuery.Source,
                            Top = new IntegerLiteral { Value = "1" }
                        };
                    }

                    // We need a non-null value to use
                    if (innerSchemaPrimaryKey == null)
                    {
                        innerSchemaPrimaryKey = $"Expr{++_colNameCounter}";

                        if (!(innerQuery.Source is ComputeScalarNode computeScalar))
                        {
                            computeScalar = new ComputeScalarNode { Source = innerQuery.Source };
                            innerQuery.Source = computeScalar;
                        }

                        computeScalar.Columns[innerSchemaPrimaryKey] = new IntegerLiteral { Value = "1" };
                    }

                    // We can spool the results for reuse each time
                    innerQuery.Source = new TableSpoolNode
                    {
                        Source = innerQuery.Source,
                        SpoolType = SpoolType.Lazy
                    };

                    testColumn = $"Expr{++_colNameCounter}";

                    join = new NestedLoopNode
                    {
                        LeftSource = source,
                        RightSource = innerQuery.Source,
                        JoinType = QualifiedJoinType.LeftOuter,
                        SemiJoin = true,
                        OuterReferences = references,
                        DefinedValues =
                        {
                            [testColumn] = innerSchemaPrimaryKey
                        }
                    };
                }
                else if (UseMergeJoin(source, innerQuery.Source, references, null, null, true, out testColumn, out var merge))
                {
                    join = merge;
                }
                else
                {
                    // We need to use nested loops for correlated subqueries
                    // TODO: We could use a hash join where there is a simple correlation, but followed by a distinct node to eliminate duplicates
                    // We could also move the correlation criteria out of the subquery and into the join condition. We would then make one request to
                    // get all the related records and spool that in memory to get the relevant results in the nested loop. Need to understand how 
                    // many rows are likely from the outer query to work out if this is going to be more efficient or not.
                    if (innerQuery.Source is ISingleSourceExecutionPlanNode loopRightSourceSimple)
                        InsertCorrelatedSubquerySpool(loopRightSourceSimple, source, hints, parameterTypes, references.Values.ToArray());

                    // We only need one record to check for EXISTS
                    if (!(innerQuery.Source is TopNode) && !(innerQuery.Source is OffsetFetchNode))
                    {
                        innerQuery.Source = new TopNode
                        {
                            Source = innerQuery.Source,
                            Top = new IntegerLiteral { Value = "1" }
                        };
                    }

                    // We need a non-null value to use
                    if (innerSchemaPrimaryKey == null)
                    {
                        innerSchemaPrimaryKey = $"Expr{++_colNameCounter}";

                        if (!(innerQuery.Source is ComputeScalarNode computeScalar))
                        {
                            computeScalar = new ComputeScalarNode { Source = innerQuery.Source };
                            innerQuery.Source = computeScalar;
                        }

                        computeScalar.Columns[innerSchema.PrimaryKey] = new IntegerLiteral { Value = "1" };
                    }

                    var definedValue = $"Expr{++_colNameCounter}";

                    join = new NestedLoopNode
                    {
                        LeftSource = source,
                        RightSource = innerQuery.Source,
                        OuterReferences = references,
                        SemiJoin = true,
                        DefinedValues = { [definedValue] = innerSchemaPrimaryKey }
                    };

                    testColumn = definedValue;
                }

                join.JoinType = QualifiedJoinType.LeftOuter;

                rewrites[existsSubquery] = new BooleanIsNullExpression
                {
                    IsNot = true,
                    Expression = testColumn.ToColumnReference()
                };

                source = join;
            }

            query.Accept(new BooleanRewriteVisitor(rewrites));

            return source;
        }

        private IDataExecutionPlanNodeInternal ConvertHavingClause(IDataExecutionPlanNodeInternal source, IList<OptimizerHint> hints, HavingClause havingClause, IDictionary<string, DataTypeReference> parameterTypes, INodeSchema outerSchema, IDictionary<string, string> outerReferences, TSqlFragment query, INodeSchema nonAggregateSchema)
        {
            if (havingClause == null)
                return source;

            CaptureOuterReferences(outerSchema, source, havingClause, parameterTypes, outerReferences);

            var computeScalar = new ComputeScalarNode { Source = source };
            ConvertScalarSubqueries(havingClause.SearchCondition, hints, ref source, computeScalar, parameterTypes, query);

            // Validate the final expression
            havingClause.SearchCondition.GetType(source.GetSchema(DataSources, parameterTypes), nonAggregateSchema, parameterTypes);

            return new FilterNode
            {
                Filter = havingClause.SearchCondition,
                Source = source
            };
        }

        private IDataExecutionPlanNodeInternal ConvertGroupByAggregates(IDataExecutionPlanNodeInternal source, QuerySpecification querySpec, IDictionary<string, DataTypeReference> parameterTypes, INodeSchema outerSchema, IDictionary<string, string> outerReferences)
        {
            // Check if there is a GROUP BY clause or aggregate functions to convert
            if (querySpec.GroupByClause == null)
            {
                var aggregates = new AggregateCollectingVisitor();
                aggregates.GetAggregates(querySpec);
                if (aggregates.SelectAggregates.Count == 0 && aggregates.Aggregates.Count == 0)
                    return source;
            }
            else
            {
                if (querySpec.GroupByClause.All == true)
                    throw new NotSupportedQueryFragmentException("Unhandled GROUP BY ALL clause", querySpec.GroupByClause);

                if (querySpec.GroupByClause.GroupByOption != GroupByOption.None)
                    throw new NotSupportedQueryFragmentException("Unhandled GROUP BY option", querySpec.GroupByClause);
            }

            var schema = source.GetSchema(DataSources, parameterTypes);

            // Create the grouping expressions. Grouping is done on single columns only - if a grouping is a more complex expression,
            // create a new calculated column using a Compute Scalar node first.
            var groupings = new Dictionary<ScalarExpression, ColumnReferenceExpression>();

            if (querySpec.GroupByClause != null)
            {
                CaptureOuterReferences(outerSchema, source, querySpec.GroupByClause, parameterTypes, outerReferences);

                foreach (var grouping in querySpec.GroupByClause.GroupingSpecifications)
                {
                    if (!(grouping is ExpressionGroupingSpecification exprGroup))
                        throw new NotSupportedQueryFragmentException("Unhandled GROUP BY expression", grouping);

                    // Validate the GROUP BY expression
                    exprGroup.Expression.GetType(schema, null, parameterTypes);

                    if (exprGroup.Expression is ColumnReferenceExpression col)
                    {
                        schema.ContainsColumn(col.GetColumnName(), out var groupByColName);

                        if (col.GetColumnName() != groupByColName)
                        {
                            col = groupByColName.ToColumnReference();
                            exprGroup.Expression = col;
                        }
                    }
                    else
                    {
                        // Use generic name for computed columns by default. Special case for DATEPART functions which
                        // could be folded down to FetchXML directly, so make these nicer names
                        string name = null;

                        if (exprGroup.Expression is FunctionCall func &&
                            func.FunctionName.Value.Equals("DATEPART", StringComparison.OrdinalIgnoreCase) &&
                            func.Parameters.Count == 2 &&
                            func.Parameters[0] is ColumnReferenceExpression datepart &&
                            func.Parameters[1] is ColumnReferenceExpression datepartCol)
                        {
                            var partName = datepart.GetColumnName();

                            // Not all DATEPART part types are supported in FetchXML. The supported ones in FetchXML are:
                            // * day
                            // * week
                            // * month
                            // * quarter
                            // * year
                            // * fiscal period
                            // * fiscal year
                            //
                            // Fiscal period/year do not have a T-SQL equivalent
                            var partnames = new Dictionary<string, FetchXml.DateGroupingType>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["year"] = FetchXml.DateGroupingType.year,
                                ["yy"] = FetchXml.DateGroupingType.year,
                                ["yyyy"] = FetchXml.DateGroupingType.year,
                                ["quarter"] = FetchXml.DateGroupingType.quarter,
                                ["qq"] = FetchXml.DateGroupingType.quarter,
                                ["q"] = FetchXml.DateGroupingType.quarter,
                                ["month"] = FetchXml.DateGroupingType.month,
                                ["mm"] = FetchXml.DateGroupingType.month,
                                ["m"] = FetchXml.DateGroupingType.month,
                                ["day"] = FetchXml.DateGroupingType.day,
                                ["dd"] = FetchXml.DateGroupingType.day,
                                ["d"] = FetchXml.DateGroupingType.day,
                                ["week"] = FetchXml.DateGroupingType.week,
                                ["wk"] = FetchXml.DateGroupingType.week,
                                ["ww"] = FetchXml.DateGroupingType.week
                            };

                            if (partnames.TryGetValue(partName, out var dateGrouping))
                            {
                                var colName = datepartCol.GetColumnName();
                                schema.ContainsColumn(colName, out colName);

                                name = colName.Split('.').Last() + "_" + dateGrouping;
                                var baseName = name;

                                var suffix = 0;

                                while (groupings.Values.Any(grp => grp.GetColumnName().Equals(name, StringComparison.OrdinalIgnoreCase)))
                                    name = $"{baseName}_{++suffix}";
                            }
                        }

                        if (name == null)
                            name = $"Expr{++_colNameCounter}";

                        col = new ColumnReferenceExpression
                        {
                            MultiPartIdentifier = new MultiPartIdentifier
                            {
                                Identifiers =
                                {
                                    new Identifier { Value = name }
                                }
                            }
                        };
                    }

                    groupings[exprGroup.Expression] = col;
                }
            }

            if (groupings.Any(kvp => kvp.Key != kvp.Value))
            {
                var computeScalar = new ComputeScalarNode { Source = source };
                var rewrites = new Dictionary<ScalarExpression, string>();

                foreach (var calc in groupings.Where(kvp => kvp.Key != kvp.Value))
                {
                    rewrites[calc.Key] = calc.Value.GetColumnName();
                    computeScalar.Columns[calc.Value.GetColumnName()] = calc.Key;
                }

                source = computeScalar;

                querySpec.Accept(new RewriteVisitor(rewrites));
            }

            var hashMatch = new HashMatchAggregateNode
            {
                Source = source
            };

            foreach (var grouping in groupings)
                hashMatch.GroupBy.Add(grouping.Value);

            // Create the aggregate functions
            var aggregateCollector = new AggregateCollectingVisitor();
            aggregateCollector.GetAggregates(querySpec);
            var aggregateRewrites = new Dictionary<ScalarExpression, string>();

            foreach (var aggregate in aggregateCollector.Aggregates.Select(a => new { Expression = a, Alias = (string)null }).Concat(aggregateCollector.SelectAggregates.Select(s => new { Expression = (FunctionCall)s.Expression, Alias = s.ColumnName?.Identifier?.Value })))
            {
                CaptureOuterReferences(outerSchema, source, aggregate.Expression, parameterTypes, outerReferences);

                var converted = new Aggregate
                {
                    Distinct = aggregate.Expression.UniqueRowFilter == UniqueRowFilter.Distinct
                };

                converted.SqlExpression = aggregate.Expression.Parameters[0];

                switch (aggregate.Expression.FunctionName.Value.ToUpper())
                {
                    case "AVG":
                        converted.AggregateType = AggregateType.Average;
                        break;

                    case "COUNT":
                        if ((converted.SqlExpression is ColumnReferenceExpression countCol && countCol.ColumnType == ColumnType.Wildcard) || (converted.SqlExpression is Literal && !(converted.SqlExpression is NullLiteral)))
                            converted.AggregateType = AggregateType.CountStar;
                        else
                            converted.AggregateType = AggregateType.Count;
                        break;

                    case "MAX":
                        converted.AggregateType = AggregateType.Max;
                        break;

                    case "MIN":
                        converted.AggregateType = AggregateType.Min;
                        break;

                    case "SUM":
                        if (converted.SqlExpression is IntegerLiteral sumLiteral && sumLiteral.Value == "1")
                            converted.AggregateType = AggregateType.CountStar;
                        else
                            converted.AggregateType = AggregateType.Sum;
                        break;

                    default:
                        throw new NotSupportedQueryFragmentException("Unknown aggregate function", aggregate.Expression);
                }

                // Validate the aggregate expression
                if (converted.AggregateType == AggregateType.CountStar)
                    converted.SqlExpression = null;
                else
                    converted.SqlExpression.GetType(schema, null, parameterTypes);

                // Create a name for the column that holds the aggregate value in the result set.
                string aggregateName;

                if (aggregate.Alias != null)
                {
                    aggregateName = aggregate.Alias;
                }
                else if (aggregate.Expression.Parameters[0] is ColumnReferenceExpression colRef)
                {
                    if (colRef.ColumnType == ColumnType.Wildcard)
                        aggregateName = aggregate.Expression.FunctionName.Value.ToLower();
                    else
                        aggregateName = colRef.GetColumnName().Replace('.', '_') + "_" + aggregate.Expression.FunctionName.Value.ToLower();

                    if (converted.Distinct)
                        aggregateName += "_distinct";
                }
                else
                {
                    aggregateName = $"Expr{++_colNameCounter}";
                }

                hashMatch.Aggregates[aggregateName] = converted;
                aggregateRewrites[aggregate.Expression] = aggregateName;
            }

            // Use the calculated aggregate values in later parts of the query
            var visitor = new RewriteVisitor(aggregateRewrites);
            foreach (var select in querySpec.SelectElements)
                select.Accept(visitor);
            querySpec.OrderByClause?.Accept(visitor);
            querySpec.HavingClause?.Accept(visitor);
            querySpec.TopRowFilter?.Accept(visitor);
            querySpec.OffsetClause?.Accept(visitor);

            return hashMatch;
        }

        private IDataExecutionPlanNodeInternal ConvertOffsetClause(IDataExecutionPlanNodeInternal source, OffsetClause offsetClause, IDictionary<string, DataTypeReference> parameterTypes)
        {
            if (offsetClause == null)
                return source;

            var offsetType = offsetClause.OffsetExpression.GetType(null, null, parameterTypes);
            var fetchType = offsetClause.FetchExpression.GetType(null, null, parameterTypes);

            if (!SqlTypeConverter.CanChangeTypeImplicit(offsetType, typeof(SqlInt32)))
                throw new NotSupportedQueryFragmentException("Unexpected OFFSET type", offsetClause.OffsetExpression);

            if (!SqlTypeConverter.CanChangeTypeImplicit(fetchType, typeof(SqlInt32)))
                throw new NotSupportedQueryFragmentException("Unexpected FETCH type", offsetClause.FetchExpression);

            return new OffsetFetchNode
            {
                Source = source,
                Offset = offsetClause.OffsetExpression,
                Fetch = offsetClause.FetchExpression
            };
        }

        private IDataExecutionPlanNodeInternal ConvertTopClause(IDataExecutionPlanNodeInternal source, TopRowFilter topRowFilter, OrderByClause orderByClause, IDictionary<string, DataTypeReference> parameterTypes)
        {
            if (topRowFilter == null)
                return source;

            var topType = topRowFilter.Expression.GetType(null, null, parameterTypes);
            var targetType = topRowFilter.Percent ? typeof(SqlSingle) : typeof(SqlInt32);

            if (!SqlTypeConverter.CanChangeTypeImplicit(topType, targetType))
                throw new NotSupportedQueryFragmentException("Unexpected TOP type", topRowFilter.Expression);

            var tieColumns = new HashSet<string>();

            if (topRowFilter.WithTies)
            {
                if (orderByClause == null)
                    throw new NotSupportedQueryFragmentException("The TOP N WITH TIES clause is not allowed without a corresponding ORDER BY clause", topRowFilter);

                var schema = source.GetSchema(DataSources, parameterTypes);

                foreach (var sort in orderByClause.OrderByElements)
                {
                    if (!(sort.Expression is ColumnReferenceExpression sortCol))
                        throw new NotSupportedQueryFragmentException("ORDER BY must reference a column for use with TOP N WITH TIES", sort.Expression);

                    if (!schema.ContainsColumn(sortCol.GetColumnName(), out var colName))
                        throw new NotSupportedQueryFragmentException("Unknown column name", sortCol);

                    tieColumns.Add(colName);
                }

                // Ensure data is sorted by the tie columns
                if (!schema.IsSortedBy(tieColumns))
                {
                    var sort = new SortNode { Source = source };
                    sort.Sorts.AddRange(orderByClause.OrderByElements);
                    source = sort;
                }
            }

            // TOP x PERCENT requires evaluating the source twice - once to get the total count and again to get the top
            // records. Cache the results in a table spool node.
            if (topRowFilter.Percent)
                source = new TableSpoolNode { Source = source, SpoolType = SpoolType.Eager };

            return new TopNode
            {
                Source = source,
                Top = topRowFilter.Expression,
                Percent = topRowFilter.Percent,
                WithTies = topRowFilter.WithTies,
                TieColumns = topRowFilter.WithTies ? tieColumns.ToList() : null
            };
        }

        private IDataExecutionPlanNodeInternal ConvertOrderByClause(IDataExecutionPlanNodeInternal source, IList<OptimizerHint> hints, OrderByClause orderByClause, ScalarExpression[] selectList, TSqlFragment query, IDictionary<string, DataTypeReference> parameterTypes, INodeSchema outerSchema, Dictionary<string, string> outerReferences, INodeSchema nonAggregateSchema)
        {
            if (orderByClause == null)
                return source;

            CaptureOuterReferences(outerSchema, source, orderByClause, parameterTypes, outerReferences);

            var computeScalar = new ComputeScalarNode { Source = source };
            ConvertScalarSubqueries(orderByClause, hints, ref source, computeScalar, parameterTypes, query);

            var schema = source.GetSchema(DataSources, parameterTypes);
            var sort = new SortNode { Source = source };

            // Sorts can use aliases from the SELECT clause
            if (query is QuerySpecification querySpec)
            {
                var rewrites = new Dictionary<ScalarExpression, ScalarExpression>();

                foreach (var select in querySpec.SelectElements.OfType<SelectScalarExpression>())
                {
                    if (select.ColumnName == null)
                        continue;

                    rewrites[select.ColumnName.Value.ToColumnReference()] = select.Expression;
                }

                if (rewrites.Any())
                    orderByClause.Accept(new RewriteVisitor(rewrites));
            }

            // Check if any of the order expressions need pre-calculation
            var calculationRewrites = new Dictionary<ScalarExpression, ScalarExpression>();

            foreach (var orderBy in orderByClause.OrderByElements)
            {
                // If the order by element is a numeric literal, use the corresponding expression from the select list at that index
                if (orderBy.Expression is IntegerLiteral literal)
                {
                    var index = int.Parse(literal.Value, CultureInfo.InvariantCulture) - 1;

                    if (index < 0 || index >= selectList.Length)
                    {
                        throw new NotSupportedQueryFragmentException("Invalid ORDER BY index", literal)
                        {
                            Suggestion = $"Must be between 1 and {selectList.Length}"
                        };
                    }

                    orderBy.Expression = selectList[index];
                }

                // Anything complex expression should be pre-calculated
                if (!(orderBy.Expression is ColumnReferenceExpression) &&
                    !(orderBy.Expression is VariableReference) &&
                    !(orderBy.Expression is Literal))
                {
                    var calculated = ComputeScalarExpression(orderBy.Expression, hints, query, computeScalar, nonAggregateSchema, parameterTypes, ref source);
                    sort.Source = source;
                    schema = source.GetSchema(DataSources, parameterTypes);

                    calculationRewrites[orderBy.Expression] = calculated.ToColumnReference();
                }

                // Validate the expression
                orderBy.Expression.GetType(schema, nonAggregateSchema, parameterTypes);

                sort.Sorts.Add(orderBy);
            }

            // Use the calculated expressions in the sort and anywhere else that uses the same expression
            if (calculationRewrites.Any())
                query.Accept(new RewriteVisitor(calculationRewrites));

            if (computeScalar.Columns.Any())
                sort.Source = computeScalar;

            return sort;
        }

        private IDataExecutionPlanNodeInternal ConvertWhereClause(IDataExecutionPlanNodeInternal source, IList<OptimizerHint> hints, WhereClause whereClause, INodeSchema outerSchema, Dictionary<string,string> outerReferences, IDictionary<string, DataTypeReference> parameterTypes, TSqlFragment query)
        {
            if (whereClause == null)
                return source;

            if (whereClause.Cursor != null)
                throw new NotSupportedQueryFragmentException("Unsupported cursor", whereClause.Cursor);

            CaptureOuterReferences(outerSchema, source, whereClause.SearchCondition, parameterTypes, outerReferences);

            var computeScalar = new ComputeScalarNode { Source = source };
            ConvertScalarSubqueries(whereClause.SearchCondition, hints, ref source, computeScalar, parameterTypes, query);

            // Validate the final expression
            whereClause.SearchCondition.GetType(source.GetSchema(DataSources, parameterTypes), null, parameterTypes);

            return new FilterNode
            {
                Filter = whereClause.SearchCondition,
                Source = source
            };
        }

        private TSqlFragment CaptureOuterReferences(INodeSchema outerSchema, IDataExecutionPlanNodeInternal source, TSqlFragment query, IDictionary<string, DataTypeReference> parameterTypes, IDictionary<string,string> outerReferences)
        {
            if (outerSchema == null)
                return query;

            // We're in a subquery. Check if any columns in the WHERE clause are from the outer query
            // so we know which columns to pass through and rewrite the filter to use parameters
            var rewrites = new Dictionary<ScalarExpression, ScalarExpression>();
            var innerSchema = source.GetSchema(DataSources, parameterTypes);
            var columns = query.GetColumns();

            foreach (var column in columns)
            {
                // Column names could be ambiguous between the inner and outer data sources. The inner
                // data source is used in preference.
                // Ref: https://docs.microsoft.com/en-us/sql/relational-databases/performance/subqueries?view=sql-server-ver15#qualifying
                var fromInner = innerSchema.ContainsColumn(column, out _);

                if (fromInner)
                    continue;

                var fromOuter = outerSchema.ContainsColumn(column, out var outerColumn);

                if (fromOuter)
                {
                    var paramName = $"@Expr{++_colNameCounter}";
                    outerReferences.Add(outerColumn, paramName);
                    parameterTypes[paramName] = outerSchema.Schema[outerColumn];

                    rewrites.Add(
                        new ColumnReferenceExpression { MultiPartIdentifier = new MultiPartIdentifier { Identifiers = { new Identifier { Value = column } } } },
                        new VariableReference { Name = paramName });
                }
            }

            if (rewrites.Any())
                query.Accept(new RewriteVisitor(rewrites));

            if (query is ScalarExpression scalar && rewrites.TryGetValue(scalar, out var rewritten))
                return rewritten;

            return query;
        }

        private SelectNode ConvertSelectClause(IList<SelectElement> selectElements, IList<OptimizerHint> hints, IDataExecutionPlanNodeInternal node, DistinctNode distinct, TSqlFragment query, IDictionary<string, DataTypeReference> parameterTypes, INodeSchema outerSchema, IDictionary<string,string> outerReferences, INodeSchema nonAggregateSchema)
        {
            var schema = node.GetSchema(DataSources, parameterTypes);

            var select = new SelectNode
            {
                Source = node
            };

            var computeScalar = new ComputeScalarNode
            {
                Source = distinct?.Source ?? node
            };

            foreach (var element in selectElements)
            {
                CaptureOuterReferences(outerSchema, computeScalar, element, parameterTypes, outerReferences);

                if (element is SelectScalarExpression scalar)
                {
                    if (scalar.Expression is ColumnReferenceExpression col)
                    {
                        var colName = col.GetColumnName();

                        if (!schema.ContainsColumn(colName, out colName))
                        {
                            // Column name isn't valid. Use the expression extensions to throw a consistent error message
                            col.GetType(schema, nonAggregateSchema, parameterTypes);
                        }

                        var alias = scalar.ColumnName?.Value ?? col.MultiPartIdentifier.Identifiers.Last().Value;

                        select.ColumnSet.Add(new SelectColumn
                        {
                            SourceColumn = colName,
                            SourceExpression = scalar.Expression,
                            OutputColumn = alias
                        });
                    }
                    else
                    {
                        var scalarSource = distinct?.Source ?? node;
                        var alias = ComputeScalarExpression(scalar.Expression, hints, query, computeScalar, nonAggregateSchema, parameterTypes, ref scalarSource);

                        if (distinct != null)
                            distinct.Source = scalarSource;
                        else
                            node = scalarSource;

                        select.ColumnSet.Add(new SelectColumn
                        {
                            SourceColumn = alias,
                            SourceExpression = scalar.Expression,
                            OutputColumn = scalar.ColumnName?.Value ?? alias
                        });
                    }
                }
                else if (element is SelectStarExpression star)
                {
                    var colName = star.Qualifier == null ? null : String.Join(".", star.Qualifier.Identifiers.Select(id => id.Value));

                    if (colName != null && !schema.Schema.Keys.Any(col => col.StartsWith(colName + ".", StringComparison.OrdinalIgnoreCase)))
                        throw new NotSupportedQueryFragmentException("The column prefix does not match with a table name or alias name used in the query", star);

                    select.ColumnSet.Add(new SelectColumn
                    {
                        SourceColumn = colName,
                        SourceExpression = star,
                        AllColumns = true
                    });
                }
                else
                {
                    throw new NotSupportedQueryFragmentException("Unhandled SELECT element", element);
                }
            }

            if (computeScalar.Columns.Count > 0)
            {
                if (distinct != null)
                    distinct.Source = computeScalar;
                else
                    select.Source = computeScalar;
            }

            if (distinct != null)
            {
                foreach (var col in select.ColumnSet)
                {
                    if (col.AllColumns)
                    {
                        var distinctSchema = distinct.GetSchema(DataSources, parameterTypes);
                        distinct.Columns.AddRange(distinctSchema.Schema.Keys.Where(k => col.SourceColumn == null || (k.Split('.')[0] + ".*") == col.SourceColumn));
                    }
                    else
                    {
                        distinct.Columns.Add(col.SourceColumn);
                    }
                }
            }

            return select;
        }

        private string ComputeScalarExpression(ScalarExpression expression, IList<OptimizerHint> hints, TSqlFragment query, ComputeScalarNode computeScalar, INodeSchema nonAggregateSchema, IDictionary<string, DataTypeReference> parameterTypes, ref IDataExecutionPlanNodeInternal node)
        {
            var computedColumn = ConvertScalarSubqueries(expression, hints, ref node, computeScalar, parameterTypes, query);

            if (computedColumn != null)
                expression = computedColumn;

            // Check the type of this expression now so any errors can be reported
            var computeScalarSchema = computeScalar.Source.GetSchema(DataSources, parameterTypes);
            expression.GetType(computeScalarSchema, nonAggregateSchema, parameterTypes);

            var alias = $"Expr{++_colNameCounter}";
            computeScalar.Columns[alias] = expression;
            return alias;
        }

        private ColumnReferenceExpression ConvertScalarSubqueries(TSqlFragment expression, IList<OptimizerHint> hints, ref IDataExecutionPlanNodeInternal node, ComputeScalarNode computeScalar, IDictionary<string, DataTypeReference> parameterTypes, TSqlFragment query)
        {
            /*
             * Possible subquery execution plans:
             * 1. Nested loop. Simple but inefficient as ends up making at least 1 FetchXML request per outer row
             * 2. Spooled nested loop. Useful when there is no correlation and so the same results can be used for each outer record
             * 3. Spooled nested loop with correlation criteria pulled into loop. Useful when there are a large number of outer records or a small number of inner records
             * 4. Merge join. Useful when the correlation criteria is based on the equality of the primary key of the inner table
             */
            // If scalar.Expression contains a subquery, create nested loop to evaluate it in the context
            // of the current record
            var subqueryVisitor = new ScalarSubqueryVisitor();
            expression.Accept(subqueryVisitor);
            var rewrites = new Dictionary<ScalarExpression, string>();

            foreach (var subquery in subqueryVisitor.Subqueries)
            {
                var outerSchema = node.GetSchema(DataSources, parameterTypes);
                var outerReferences = new Dictionary<string, string>();
                var innerParameterTypes = parameterTypes == null ? new Dictionary<string, DataTypeReference>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, DataTypeReference>(parameterTypes, StringComparer.OrdinalIgnoreCase);
                var subqueryPlan = ConvertSelectStatement(subquery.QueryExpression, hints, outerSchema, outerReferences, innerParameterTypes);

                // Scalar subquery must return exactly one column and one row
                if (subqueryPlan.ColumnSet.Count != 1)
                    throw new NotSupportedQueryFragmentException("Scalar subquery must return exactly one column", subquery);

                string outputcol;
                var subqueryCol = subqueryPlan.ColumnSet[0].SourceColumn;
                BaseJoinNode join = null;
                if (UseMergeJoin(node, subqueryPlan.Source, outerReferences, subqueryCol, null, false, out outputcol, out var merge))
                {
                    join = merge;
                }
                else
                {
                    outputcol = $"Expr{++_colNameCounter}";

                    var loopRightSource = subqueryPlan.Source;

                    // Unless the subquery has got an explicit TOP 1 clause, insert an aggregate and assertion nodes
                    // to check for one row
                    if (!(subqueryPlan.Source is TopNode top) || !(top.Top is IntegerLiteral topValue) || topValue.Value != "1")
                    {
                        subqueryCol = $"Expr{++_colNameCounter}";
                        var rowCountCol = $"Expr{++_colNameCounter}";
                        var aggregate = new HashMatchAggregateNode
                        {
                            Source = loopRightSource,
                            Aggregates =
                            {
                                [subqueryCol] = new Aggregate
                                {
                                    AggregateType = AggregateType.First,
                                    SqlExpression = new ColumnReferenceExpression
                                    {
                                        MultiPartIdentifier = new MultiPartIdentifier
                                        {
                                            Identifiers = { new Identifier { Value = subqueryPlan.ColumnSet[0].SourceColumn } }
                                        }
                                    }
                                },
                                [rowCountCol] = new Aggregate
                                {
                                    AggregateType = AggregateType.CountStar
                                }
                            }
                        };
                        var assert = new AssertNode
                        {
                            Source = aggregate,
                            Assertion = e => e.GetAttributeValue<SqlInt32>(rowCountCol).Value <= 1,
                            ErrorMessage = "Subquery produced more than 1 row"
                        };
                        loopRightSource = assert;
                    }

                    // If the subquery is uncorrelated, add a table spool to cache the results
                    // If it is correlated, add a spool where possible closer to the data source
                    if (outerReferences.Count == 0)
                    {
                        if (EstimateRowsOut(node, parameterTypes) > 1)
                        {
                            var spool = new TableSpoolNode { Source = loopRightSource, SpoolType = SpoolType.Lazy };
                            loopRightSource = spool;
                        }
                    }
                    else if (loopRightSource is ISingleSourceExecutionPlanNode loopRightSourceSimple)
                    {
                        InsertCorrelatedSubquerySpool(loopRightSourceSimple, node, hints, parameterTypes, outerReferences.Values.ToArray());
                    }

                    // Add a nested loop to call the subquery
                    if (join == null)
                    {
                        join = new NestedLoopNode
                        {
                            LeftSource = node,
                            RightSource = loopRightSource,
                            OuterReferences = outerReferences,
                            JoinType = QualifiedJoinType.LeftOuter,
                            SemiJoin = true,
                            DefinedValues = { [outputcol] = subqueryCol }
                        };
                    }
                }

                node = join;
                computeScalar.Source = join;

                rewrites[subquery] = outputcol;
            }

            if (rewrites.Any())
                query.Accept(new RewriteVisitor(rewrites));

            if (expression is ScalarExpression scalar && rewrites.ContainsKey(scalar))
                return new ColumnReferenceExpression { MultiPartIdentifier = new MultiPartIdentifier { Identifiers = { new Identifier { Value = rewrites[scalar] } } } };

            return null;
        }

        private bool UseMergeJoin(IDataExecutionPlanNodeInternal node, IDataExecutionPlanNode subqueryPlan, Dictionary<string, string> outerReferences, string subqueryCol, string inPredicateCol, bool semiJoin, out string outputCol, out MergeJoinNode merge)
        {
            outputCol = null;
            merge = null;

            // We can use a merge join for a scalar subquery when the subquery is simply SELECT [TOP 1] <column> FROM <table> WHERE <table>.<key> = <outertable>.<column>
            // The filter must be on the inner table's primary key
            var subNode = subqueryPlan;

            var alias = subNode as AliasNode;
            if (alias != null)
                subNode = alias.Source;

            if (subNode is TopNode top && top.Top is IntegerLiteral topLiteral && topLiteral.Value == "1")
                subNode = top.Source;

            var filter = subNode as FilterNode;
            if (filter != null)
                subNode = filter.Source;
            else if (inPredicateCol == null)
                return false;

            if (!(subNode is FetchXmlScan fetch))
                return false;

            var outerKey = (string)null;
            var innerKey = (string)null;

            if (inPredicateCol != null)
            {
                outerKey = inPredicateCol;
                innerKey = subqueryCol;
            }
            else
            {
                if (!(filter.Filter is BooleanComparisonExpression cmp))
                    return false;

                if (cmp.ComparisonType != BooleanComparisonType.Equals)
                    return false;

                var col1 = cmp.FirstExpression as ColumnReferenceExpression;
                var var1 = cmp.FirstExpression as VariableReference;

                var col2 = cmp.SecondExpression as ColumnReferenceExpression;
                var var2 = cmp.SecondExpression as VariableReference;

                var col = col1 ?? col2;
                var var = var1 ?? var2;

                if (col == null || var == null)
                    return false;

                foreach (var outerReference in outerReferences)
                {
                    if (outerReference.Value == var.Name)
                    {
                        outerKey = outerReference.Key;
                        break;
                    }
                }

                innerKey = col.GetColumnName();
            }

            if (outerKey == null)
                return false;

            var outerSchema = node.GetSchema(DataSources, null);
            var innerSchema = fetch.GetSchema(DataSources, null);

            if (!outerSchema.ContainsColumn(outerKey, out outerKey) ||
                !innerSchema.ContainsColumn(innerKey, out innerKey))
                return false;

            if (outerSchema.PrimaryKey != outerKey &&
                innerSchema.PrimaryKey != innerKey)
                return false;

            // Give the inner fetch a unique alias and update the name of the inner key
            if (alias != null)
                fetch.Alias = alias.Alias;
            else
                fetch.Alias = $"Expr{++_colNameCounter}";

            var rightAttribute = innerKey.ToColumnReference();
            if (rightAttribute.MultiPartIdentifier.Identifiers.Count == 2)
                rightAttribute.MultiPartIdentifier.Identifiers[0].Value = fetch.Alias;

            // Add the required column with the expected alias (used for scalar subqueries and IN predicates, not for CROSS/OUTER APPLY
            if (subqueryCol != null)
            {
                var attr = new FetchXml.FetchAttributeType { name = subqueryCol.Split('.').Last() };
                fetch.Entity.AddItem(attr);
                outputCol = fetch.Alias + "." + attr.name;
            }

            merge = new MergeJoinNode
            {
                LeftSource = node,
                LeftAttribute = outerKey.ToColumnReference(),
                RightSource = inPredicateCol != null ? (IDataExecutionPlanNodeInternal) filter ?? fetch : fetch,
                RightAttribute = rightAttribute,
                JoinType = QualifiedJoinType.LeftOuter
            };

            if (semiJoin)
            {
                // Regenerate the schema after changing the alias
                innerSchema = fetch.GetSchema(DataSources, null);

                if (innerSchema.PrimaryKey != rightAttribute.GetColumnName() && !(merge.RightSource is DistinctNode))
                {
                    merge.RightSource = new DistinctNode
                    {
                        Source = merge.RightSource,
                        Columns = { rightAttribute.GetColumnName() }
                    };
                }

                merge.SemiJoin = true;
                var definedValue = $"Expr{++_colNameCounter}";
                merge.DefinedValues[definedValue] = outputCol ?? rightAttribute.GetColumnName();
                outputCol = definedValue;
            }

            return true;
        }
        
        private void InsertCorrelatedSubquerySpool(ISingleSourceExecutionPlanNode node, IDataExecutionPlanNode outerSource, IList<OptimizerHint> hints, IDictionary<string, DataTypeReference> parameterTypes, string[] outerReferences)
        {
            if (hints.Any(hint => hint.HintKind == OptimizerHintKind.NoPerformanceSpool))
                return;

            // Look for a simple case where there is a reference to the outer table in a filter node. Extract the minimal
            // amount of that filter to a new filter node and place a table spool between the correlated filter and its source

            // Skip over simple leading nodes to try to find a Filter node
            var lastCorrelatedStep = node;
            ISingleSourceExecutionPlanNode parentNode = null;
            FilterNode filter = null;

            while (node != null)
            {
                if (node is FilterNode f)
                {
                    filter = f;
                    break;
                }

                if (node is FetchXmlScan)
                    break;

                parentNode = node;

                if (node is IDataExecutionPlanNodeInternal dataNode && dataNode.GetVariables(false).Intersect(outerReferences).Any())
                    lastCorrelatedStep = node;

                node = node.Source as ISingleSourceExecutionPlanNode;
            }

            // If anything in the filter's source uses the outer reference we can't spool ths results
            if (filter != null && filter.Source.GetVariables(true).Intersect(outerReferences).Any())
                return;

            if (filter != null && filter.Filter.GetVariables().Any())
            {
                // The filter is correlated. Check if there's any non-correlated criteria we can split out into a separate node
                // that could be folded into the data source first
                if (SplitCorrelatedCriteria(filter.Filter, out var correlatedFilter, out var nonCorrelatedFilter))
                {
                    filter.Filter = correlatedFilter;
                    filter.Source = new FilterNode
                    {
                        Filter = nonCorrelatedFilter,
                        Source = filter.Source
                    };
                }

                lastCorrelatedStep = filter;
            }

            if (lastCorrelatedStep == null)
                return;

            // Check the estimated counts for the outer loop and the source at the point we'd insert the spool
            // If the outer loop is non-trivial (>= 100 rows) or the inner loop is small (<= 5000 records) then we want
            // to use the spool.
            var outerCount = EstimateRowsOut((IDataExecutionPlanNodeInternal) outerSource, parameterTypes);
            var innerCount = outerCount >= 100 ? -1 : EstimateRowsOut(lastCorrelatedStep.Source, parameterTypes);

            if (outerCount >= 100 || innerCount <= 5000)
            {
                var spool = new TableSpoolNode
                {
                    Source = lastCorrelatedStep.Source,
                    SpoolType = SpoolType.Lazy
                };

                lastCorrelatedStep.Source = spool;
            }
        }

        private int EstimateRowsOut(IExecutionPlanNode source, IDictionary<string, DataTypeReference> parameterTypes)
        {
            if (source is IDataExecutionPlanNodeInternal dataNode)
            {
                dataNode.EstimateRowsOut(DataSources, Options, parameterTypes);
                return dataNode.EstimatedRowsOut;
            }
            else
            {
                foreach (var child in source.GetSources())
                {
                    EstimateRowsOut(child, parameterTypes);
                }
            }

            return 0;
        }

        private bool SplitCorrelatedCriteria(BooleanExpression filter, out BooleanExpression correlatedFilter, out BooleanExpression nonCorrelatedFilter)
        {
            correlatedFilter = null;
            nonCorrelatedFilter = null;

            if (!filter.GetVariables().Any())
            {
                nonCorrelatedFilter = filter;
                return true;
            }

            if (filter is BooleanBinaryExpression bin && bin.BinaryExpressionType == BooleanBinaryExpressionType.And)
            {
                var splitLhs = SplitCorrelatedCriteria(bin.FirstExpression, out var correlatedLhs, out var nonCorrelatedLhs);
                var splitRhs = SplitCorrelatedCriteria(bin.SecondExpression, out var correlatedRhs, out var nonCorrelatedRhs);

                if (splitLhs || splitRhs)
                {
                    if (correlatedLhs != null && correlatedRhs != null)
                    {
                        correlatedFilter = new BooleanBinaryExpression
                        {
                            FirstExpression = correlatedLhs,
                            BinaryExpressionType = BooleanBinaryExpressionType.And,
                            SecondExpression = correlatedRhs
                        };
                    }
                    else
                    {
                        correlatedFilter = correlatedLhs ?? correlatedRhs;
                    }

                    if (nonCorrelatedLhs != null && nonCorrelatedRhs != null)
                    {
                        nonCorrelatedFilter = new BooleanBinaryExpression
                        {
                            FirstExpression = nonCorrelatedLhs,
                            BinaryExpressionType = BooleanBinaryExpressionType.And,
                            SecondExpression = nonCorrelatedRhs
                        };
                    }
                    else
                    {
                        nonCorrelatedFilter = nonCorrelatedLhs ?? nonCorrelatedRhs;
                    }

                    return true;
                }
            }

            correlatedFilter = filter;
            return false;
        }

        private IDataExecutionPlanNodeInternal ConvertFromClause(IList<TableReference> tables, IList<OptimizerHint> hints, TSqlFragment query, INodeSchema outerSchema, Dictionary<string, string> outerReferences, IDictionary<string, DataTypeReference> parameterTypes)
        {
            var node = ConvertTableReference(tables[0], hints, query, outerSchema, outerReferences, parameterTypes);

            for (var i = 1; i < tables.Count; i++)
            {
                var nextTable = ConvertTableReference(tables[i], hints, query, outerSchema, outerReferences, parameterTypes);

                // TODO: See if we can lift a join predicate from the WHERE clause
                nextTable = new TableSpoolNode { Source = nextTable, SpoolType = SpoolType.Lazy };

                node = new NestedLoopNode { LeftSource = node, RightSource = nextTable };
            }

            return node;
        }

        private IDataExecutionPlanNodeInternal ConvertTableReference(TableReference reference, IList<OptimizerHint> hints, TSqlFragment query, INodeSchema outerSchema, Dictionary<string, string> outerReferences, IDictionary<string, DataTypeReference> parameterTypes)
        {
            if (reference is NamedTableReference table)
            {
                var dataSource = SelectDataSource(table.SchemaObject);
                var entityName = table.SchemaObject.BaseIdentifier.Value;

                if (table.SchemaObject.SchemaIdentifier?.Value?.Equals("metadata", StringComparison.OrdinalIgnoreCase) == true)
                {
                    // We're asking for metadata - check the type
                    if (entityName.Equals("entity", StringComparison.OrdinalIgnoreCase))
                    {
                        return new MetadataQueryNode
                        {
                            DataSource = dataSource.Name,
                            MetadataSource = MetadataSource.Entity,
                            EntityAlias = table.Alias?.Value ?? entityName
                        };
                    }

                    if (entityName.Equals("attribute", StringComparison.OrdinalIgnoreCase))
                    {
                        return new MetadataQueryNode
                        {
                            DataSource = dataSource.Name,
                            MetadataSource = MetadataSource.Attribute,
                            AttributeAlias = table.Alias?.Value ?? entityName
                        };
                    }

                    if (entityName.Equals("relationship_1_n", StringComparison.OrdinalIgnoreCase))
                    {
                        return new MetadataQueryNode
                        {
                            DataSource = dataSource.Name,
                            MetadataSource = MetadataSource.OneToManyRelationship,
                            OneToManyRelationshipAlias = table.Alias?.Value ?? entityName
                        };
                    }

                    if (entityName.Equals("relationship_n_1", StringComparison.OrdinalIgnoreCase))
                    {
                        return new MetadataQueryNode
                        {
                            DataSource = dataSource.Name,
                            MetadataSource = MetadataSource.ManyToOneRelationship,
                            ManyToOneRelationshipAlias = table.Alias?.Value ?? entityName
                        };
                    }

                    if (entityName.Equals("relationship_n_n", StringComparison.OrdinalIgnoreCase))
                    {
                        return new MetadataQueryNode
                        {
                            DataSource = dataSource.Name,
                            MetadataSource = MetadataSource.ManyToManyRelationship,
                            ManyToManyRelationshipAlias = table.Alias?.Value ?? entityName
                        };
                    }

                    if (entityName.Equals("globaloptionset", StringComparison.OrdinalIgnoreCase))
                    {
                        return new GlobalOptionSetQueryNode
                        {
                            DataSource = dataSource.Name,
                            Alias = table.Alias?.Value ?? entityName
                        };
                    }

                    throw new NotSupportedQueryFragmentException("Unknown table name", table);
                }

                if (!String.IsNullOrEmpty(table.SchemaObject.SchemaIdentifier?.Value) && !table.SchemaObject.SchemaIdentifier.Value.Equals("dbo", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedQueryFragmentException("Unknown table name", table);

                // Validate the entity name
                EntityMetadata meta;

                try
                {
                    meta = dataSource.Metadata[entityName];
                }
                catch (FaultException ex)
                {
                    throw new NotSupportedQueryFragmentException(ex.Message, reference);
                }

                var unsupportedHint = table.TableHints.FirstOrDefault(hint => hint.HintKind != TableHintKind.NoLock);
                if (unsupportedHint != null)
                    throw new NotSupportedQueryFragmentException("Unsupported table hint", unsupportedHint);

                if (table.TableSampleClause != null)
                    throw new NotSupportedQueryFragmentException("Unsupported table sample clause", table.TableSampleClause);

                if (table.TemporalClause != null)
                    throw new NotSupportedQueryFragmentException("Unsupported temporal clause", table.TemporalClause);

                // Convert to a simple FetchXML source
                return new FetchXmlScan
                {
                    DataSource = dataSource.Name,
                    FetchXml = new FetchXml.FetchType
                    {
                        nolock = table.TableHints.Any(hint => hint.HintKind == TableHintKind.NoLock),
                        Items = new object[]
                        {
                            new FetchXml.FetchEntityType
                            {
                                name = meta.LogicalName
                            }
                        },
                        options = ConvertQueryHints(hints)
                    },
                    Alias = table.Alias?.Value ?? entityName,
                    ReturnFullSchema = true
                };
            }

            if (reference is QualifiedJoin join)
            {
                // If the join involves the primary key of one table we can safely use a merge join.
                // Otherwise use a nested loop join
                var lhs = ConvertTableReference(join.FirstTableReference, hints, query, outerSchema, outerReferences, parameterTypes);
                var rhs = ConvertTableReference(join.SecondTableReference, hints, query, outerSchema, outerReferences, parameterTypes);
                var lhsSchema = lhs.GetSchema(DataSources, parameterTypes);
                var rhsSchema = rhs.GetSchema(DataSources, parameterTypes);

                var joinConditionVisitor = new JoinConditionVisitor(lhsSchema, rhsSchema);
                join.SearchCondition.Accept(joinConditionVisitor);

                // If we didn't find any join criteria equating two columns in the table, try again
                // but allowing computed columns instead. This lets us use more efficient join types (merge or hash join)
                // by pre-computing the values of the expressions to use as the join keys
                if (joinConditionVisitor.LhsKey == null || joinConditionVisitor.RhsKey == null)
                {
                    joinConditionVisitor = new JoinConditionVisitor(lhsSchema, rhsSchema);
                    joinConditionVisitor.AllowExpressions = true;

                    join.SearchCondition.Accept(joinConditionVisitor);

                    if (joinConditionVisitor.LhsExpression != null && joinConditionVisitor.RhsExpression != null)
                    {
                        // Calculate the two join expressions
                        if (joinConditionVisitor.LhsKey == null)
                        {
                            if (!(lhs is ComputeScalarNode lhsComputeScalar))
                            {
                                lhsComputeScalar = new ComputeScalarNode { Source = lhs };
                                lhs = lhsComputeScalar;
                            }

                            var lhsColumn = ComputeScalarExpression(joinConditionVisitor.LhsExpression, hints, query, lhsComputeScalar, null, parameterTypes, ref lhs);
                            joinConditionVisitor.LhsKey = new ColumnReferenceExpression { MultiPartIdentifier = new MultiPartIdentifier { Identifiers = { new Identifier { Value = lhsColumn } } } };
                        }

                        if (joinConditionVisitor.RhsKey == null)
                        {
                            if (!(rhs is ComputeScalarNode rhsComputeScalar))
                            {
                                rhsComputeScalar = new ComputeScalarNode { Source = rhs };
                                rhs = rhsComputeScalar;
                            }

                            var rhsColumn = ComputeScalarExpression(joinConditionVisitor.RhsExpression, hints, query, rhsComputeScalar, null, parameterTypes, ref lhs);
                            joinConditionVisitor.RhsKey = new ColumnReferenceExpression { MultiPartIdentifier = new MultiPartIdentifier { Identifiers = { new Identifier { Value = rhsColumn } } } };
                        }
                    }
                }

                BaseJoinNode joinNode;

                if (joinConditionVisitor.LhsKey != null && joinConditionVisitor.RhsKey != null && joinConditionVisitor.LhsKey.GetColumnName() == lhsSchema.PrimaryKey)
                {
                    joinNode = new MergeJoinNode
                    {
                        LeftSource = lhs,
                        LeftAttribute = joinConditionVisitor.LhsKey,
                        RightSource = rhs,
                        RightAttribute = joinConditionVisitor.RhsKey,
                        JoinType = join.QualifiedJoinType,
                        AdditionalJoinCriteria = join.SearchCondition.RemoveCondition(joinConditionVisitor.JoinCondition)
                    };
                }
                else if (joinConditionVisitor.LhsKey != null && joinConditionVisitor.RhsKey != null && joinConditionVisitor.RhsKey.GetColumnName() == rhsSchema.PrimaryKey)
                {
                    joinNode = new MergeJoinNode
                    {
                        LeftSource = rhs,
                        LeftAttribute = joinConditionVisitor.RhsKey,
                        RightSource = lhs,
                        RightAttribute = joinConditionVisitor.LhsKey,
                        AdditionalJoinCriteria = join.SearchCondition.RemoveCondition(joinConditionVisitor.JoinCondition)
                    };

                    switch (join.QualifiedJoinType)
                    {
                        case QualifiedJoinType.Inner:
                            joinNode.JoinType = QualifiedJoinType.Inner;
                            break;

                        case QualifiedJoinType.LeftOuter:
                            joinNode.JoinType = QualifiedJoinType.RightOuter;
                            break;

                        case QualifiedJoinType.RightOuter:
                            joinNode.JoinType = QualifiedJoinType.LeftOuter;
                            break;

                        case QualifiedJoinType.FullOuter:
                            joinNode.JoinType = QualifiedJoinType.FullOuter;
                            break;
                    }
                }
                else if (joinConditionVisitor.LhsKey != null && joinConditionVisitor.RhsKey != null)
                {
                    joinNode = new HashJoinNode
                    {
                        LeftSource = lhs,
                        LeftAttribute = joinConditionVisitor.LhsKey,
                        RightSource = rhs,
                        RightAttribute = joinConditionVisitor.RhsKey,
                        JoinType = join.QualifiedJoinType,
                        AdditionalJoinCriteria = join.SearchCondition.RemoveCondition(joinConditionVisitor.JoinCondition)
                    };
                }
                else
                {
                    joinNode = new NestedLoopNode
                    {
                        LeftSource = lhs,
                        RightSource = rhs,
                        JoinType = join.QualifiedJoinType,
                        JoinCondition = join.SearchCondition
                    };
                }

                // Validate the join condition
                var joinSchema = joinNode.GetSchema(DataSources, parameterTypes);
                join.SearchCondition.GetType(joinSchema, null, parameterTypes);

                return joinNode;
            }

            if (reference is QueryDerivedTable queryDerivedTable)
            {
                if (queryDerivedTable.Columns.Count > 0)
                    throw new NotSupportedQueryFragmentException("Unhandled query derived table column list", queryDerivedTable);

                var select = ConvertSelectStatement(queryDerivedTable.QueryExpression, hints, outerSchema, outerReferences, parameterTypes);
                var alias = new AliasNode(select, queryDerivedTable.Alias);

                return alias;
            }

            if (reference is InlineDerivedTable inlineDerivedTable)
                return ConvertInlineDerivedTable(inlineDerivedTable, hints, outerSchema, outerReferences, parameterTypes);

            if (reference is UnqualifiedJoin unqualifiedJoin)
            {
                var lhs = ConvertTableReference(unqualifiedJoin.FirstTableReference, hints, query, outerSchema, outerReferences, parameterTypes);
                IDataExecutionPlanNodeInternal rhs;
                Dictionary<string, string> lhsReferences;

                if (unqualifiedJoin.UnqualifiedJoinType == UnqualifiedJoinType.CrossJoin)
                {
                    rhs = ConvertTableReference(unqualifiedJoin.SecondTableReference, hints, query, outerSchema, outerReferences, parameterTypes);
                    lhsReferences = null;
                }
                else
                {
                    // CROSS APPLY / OUTER APPLY - treat the second table as a correlated subquery
                    var lhsSchema = lhs.GetSchema(DataSources, parameterTypes);
                    lhsReferences = new Dictionary<string, string>();
                    var innerParameterTypes = parameterTypes == null ? new Dictionary<string, DataTypeReference>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, DataTypeReference>(parameterTypes, StringComparer.OrdinalIgnoreCase);
                    var subqueryPlan = ConvertTableReference(unqualifiedJoin.SecondTableReference, hints, query, lhsSchema, lhsReferences, innerParameterTypes);
                    rhs = subqueryPlan;

                    // If the subquery is uncorrelated, add a table spool to cache the results
                    // If it is correlated, add a spool where possible closer to the data source
                    if (lhsReferences.Count == 0)
                    {
                        var spool = new TableSpoolNode { Source = rhs, SpoolType = SpoolType.Lazy };
                        rhs = spool;
                    }
                    else if (UseMergeJoin(lhs, subqueryPlan, lhsReferences, null, null, false, out _, out var merge))
                    {
                        if (unqualifiedJoin.UnqualifiedJoinType == UnqualifiedJoinType.CrossApply)
                            merge.JoinType = QualifiedJoinType.Inner;

                        return merge;
                    }
                    else if (rhs is ISingleSourceExecutionPlanNode loopRightSourceSimple)
                    {
                        InsertCorrelatedSubquerySpool(loopRightSourceSimple, lhs, hints, parameterTypes, lhsReferences.Values.ToArray());
                    }
                }

                // For cross joins there is no outer reference so the entire result can be spooled for reuse
                if (unqualifiedJoin.UnqualifiedJoinType == UnqualifiedJoinType.CrossJoin)
                    rhs = new TableSpoolNode { Source = rhs, SpoolType = SpoolType.Lazy };
                
                return new NestedLoopNode
                {
                    LeftSource = lhs,
                    RightSource = rhs,
                    JoinType = unqualifiedJoin.UnqualifiedJoinType == UnqualifiedJoinType.OuterApply ? QualifiedJoinType.LeftOuter : QualifiedJoinType.Inner,
                    OuterReferences = lhsReferences
                };
            }

            throw new NotSupportedQueryFragmentException("Unhandled table reference", reference);
        }

        private IDataExecutionPlanNodeInternal ConvertInlineDerivedTable(InlineDerivedTable inlineDerivedTable, IList<OptimizerHint> hints, INodeSchema outerSchema, Dictionary<string, string> outerReferences, IDictionary<string, DataTypeReference> parameterTypes)
        {
            // Check all the rows have the expected number of values and column names are unique
            var columnNames = inlineDerivedTable.Columns.Select(col => col.Value).ToList();

            for (var i = 1; i < columnNames.Count; i++)
            {
                if (columnNames.Take(i).Any(prevCol => prevCol.Equals(columnNames[i], StringComparison.OrdinalIgnoreCase)))
                    throw new NotSupportedQueryFragmentException("Duplicate column name", inlineDerivedTable.Columns[i]);
            }

            var firstMismatchRow = inlineDerivedTable.RowValues.FirstOrDefault(row => row.ColumnValues.Count != columnNames.Count);
            if (firstMismatchRow != null)
                throw new NotSupportedQueryFragmentException($"Expected {columnNames.Count} columns, got {firstMismatchRow.ColumnValues.Count}", firstMismatchRow);

            var rows = inlineDerivedTable.RowValues.Select(row => CreateSelectRow(row, inlineDerivedTable.Columns)).ToArray();
            var select = (QueryExpression) rows[0];

            for (var i = 1; i < rows.Length; i++)
            {
                select = new BinaryQueryExpression
                {
                    FirstQueryExpression = select,
                    SecondQueryExpression = rows[i],
                    All = true
                };
            }

            var converted = ConvertSelectStatement(select, hints, outerSchema, outerReferences, parameterTypes);
            var source = converted.Source;

            // Make sure expected column names are used
            if (source is ConcatenateNode concat)
            {
                for (var i = 0; i < inlineDerivedTable.Columns.Count; i++)
                {
                    concat.ColumnSet[i].OutputColumn = inlineDerivedTable.Columns[i].Value;
                    converted.ColumnSet[i].SourceColumn = inlineDerivedTable.Columns[i].Value;
                }
            }
            else if (source is ComputeScalarNode compute)
            {
                for (var i = 0; i < converted.ColumnSet.Count; i++)
                {
                    if (converted.ColumnSet[i].SourceColumn != converted.ColumnSet[i].OutputColumn && compute.Columns.TryGetValue(converted.ColumnSet[i].SourceColumn, out var expr))
                    {
                        compute.Columns[converted.ColumnSet[i].OutputColumn] = expr;
                        compute.Columns.Remove(converted.ColumnSet[i].SourceColumn);
                        converted.ColumnSet[i].SourceColumn = converted.ColumnSet[i].OutputColumn;
                    }
                }
            }

            // Make sure expected table name is used
            if (!String.IsNullOrEmpty(inlineDerivedTable.Alias?.Value))
                source = new AliasNode(converted, inlineDerivedTable.Alias);

            return source;
        }

        private QuerySpecification CreateSelectRow(RowValue row, IList<Identifier> columns)
        {
            var querySpec = new QuerySpecification();
            
            for (var i = 0; i < columns.Count; i++)
            {
                querySpec.SelectElements.Add(new SelectScalarExpression
                {
                    Expression = row.ColumnValues[i],
                    ColumnName = new IdentifierOrValueExpression { Identifier = columns[i] }
                });
            }

            return querySpec;
        }

        private string ConvertQueryHints(IList<OptimizerHint> hints)
        {
            if (hints.Count == 0)
                return null;

            return String.Join(",", hints.Select(hint =>
                {
                    switch (hint.HintKind)
                    {
                        case OptimizerHintKind.OptimizeFor:
                            if (!((OptimizeForOptimizerHint)hint).IsForUnknown)
                                return null;

                            return "OptimizeForUnknown";

                        case OptimizerHintKind.ForceOrder:
                            return "ForceOrder";

                        case OptimizerHintKind.Recompile:
                            return "Recompile";

                        case OptimizerHintKind.Unspecified:
                            if (!(hint is UseHintList useHint))
                                return null;

                            return String.Join(",", useHint.Hints.Select(hintLiteral =>
                            {
                                switch (hintLiteral.Value.ToUpperInvariant())
                                {
                                    case "DISABLE_OPTIMIZER_ROWGOAL":
                                        return "DisableRowGoal";

                                    case "ENABLE_QUERY_OPTIMIZER_HOTFIXES":
                                        return "EnableOptimizerHotfixes";

                                    default:
                                        return null;
                                }
                            }));

                        case OptimizerHintKind.LoopJoin:
                            return "LoopJoin";

                        case OptimizerHintKind.MergeJoin:
                            return "MergeJoin";

                        case OptimizerHintKind.HashJoin:
                            return "HashJoin";

                        case OptimizerHintKind.NoPerformanceSpool:
                            return "NO_PERFORMANCE_SPOOL";

                        case OptimizerHintKind.MaxRecursion:
                            return $"MaxRecursion={((LiteralOptimizerHint)hint).Value.Value}";

                        default:
                            return null;
                    }
                })
                .Where(hint => hint != null));
        }
    }
}
