﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MarkMpn.Sql4Cds.Engine.FetchXml;
using MarkMpn.Sql4Cds.Engine.Visitors;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Microsoft.Xrm.Sdk;

namespace MarkMpn.Sql4Cds.Engine.ExecutionPlan
{
    public class HashMatchAggregateNode : BaseNode
    {
        public List<ColumnReferenceExpression> GroupBy { get; } = new List<ColumnReferenceExpression>();

        public Dictionary<string, Aggregate> Aggregates { get; } = new Dictionary<string, Aggregate>();

        public IExecutionPlanNode Source { get; set; }

        public override IEnumerable<Entity> Execute(IOrganizationService org, IAttributeMetadataCache metadata, IQueryExecutionOptions options)
        {
            throw new NotImplementedException();
        }

        public override IEnumerable<string> GetRequiredColumns()
        {
            foreach (var group in GroupBy)
            {
                foreach (var col in group.GetColumns())
                    yield return col;
            }

            foreach (var aggregate in Aggregates.Values)
            {
                if (aggregate.Expression == null)
                    continue;

                foreach (var col in aggregate.Expression.GetColumns())
                    yield return col;
            }
        }

        public override NodeSchema GetSchema(IAttributeMetadataCache metadata)
        {
            var sourceSchema = Source.GetSchema(metadata);
            var schema = new NodeSchema();

            foreach (var group in GroupBy)
            {
                var colName = group.GetColumnName();
                sourceSchema.ContainsColumn(colName, out var normalized);
                schema.Schema[normalized] = sourceSchema.Schema[normalized];

                foreach (var alias in sourceSchema.Aliases.Where(a => a.Value.Contains(normalized)))
                {
                    if (!schema.Aliases.TryGetValue(alias.Key, out var aliases))
                    {
                        aliases = new List<string>();
                        schema.Aliases[alias.Key] = aliases;
                    }

                    aliases.Add(normalized);
                }
            }

            return schema;
        }

        public override IEnumerable<IExecutionPlanNode> GetSources()
        {
            yield return Source;
        }

        public override IExecutionPlanNode MergeNodeDown(IAttributeMetadataCache metadata, IQueryExecutionOptions options)
        {
            Source = Source.MergeNodeDown(metadata, options);

            if (Source is FetchXmlScan || Source is ComputeScalarNode computeScalar && computeScalar.Source is FetchXmlScan)
            {
                // Check if all the aggregates & groupings can be done in FetchXML. Can only convert them if they can ALL
                // be handled - if any one needs to be calculated manually, we need to calculate them all
                foreach (var agg in Aggregates)
                {
                    if (agg.Value.Expression != null && !(agg.Value.Expression is ColumnReferenceExpression))
                        return this;

                    if (agg.Value.Distinct && agg.Value.AggregateType != ExecutionPlan.AggregateType.Count)
                        return this;
                }

                var fetchXml = Source as FetchXmlScan;
                computeScalar = Source as ComputeScalarNode;

                var partnames = new Dictionary<string, FetchXml.DateGroupingType>(StringComparer.OrdinalIgnoreCase)
                {
                    ["year"] = DateGroupingType.year,
                    ["yy"] = DateGroupingType.year,
                    ["yyyy"] = DateGroupingType.year,
                    ["quarter"] = DateGroupingType.quarter,
                    ["qq"] = DateGroupingType.quarter,
                    ["q"] = DateGroupingType.quarter,
                    ["month"] = DateGroupingType.month,
                    ["mm"] = DateGroupingType.month,
                    ["m"] = DateGroupingType.month,
                    ["day"] = DateGroupingType.day,
                    ["dd"] = DateGroupingType.day,
                    ["d"] = DateGroupingType.day,
                    ["week"] = DateGroupingType.week,
                    ["wk"] = DateGroupingType.week,
                    ["ww"] = DateGroupingType.week
                };

                if (computeScalar != null)
                {
                    fetchXml = (FetchXmlScan)computeScalar.Source;

                    // Groupings may be on DATEPART function, which will have been split into separate Compute Scalar node. Check if all the scalar values
                    // being computed are DATEPART functions that can be converted to FetchXML and are used as groupings
                    foreach (var scalar in computeScalar.Columns)
                    {
                        if (!(scalar.Value is FunctionCall func) ||
                            !func.FunctionName.Value.Equals("DATEPART", StringComparison.OrdinalIgnoreCase) ||
                            func.Parameters.Count != 2 ||
                            !(func.Parameters[0] is ColumnReferenceExpression datePartType) ||
                            !(func.Parameters[1] is ColumnReferenceExpression datePartCol))
                            return this;

                        if (!GroupBy.Any(g => g.MultiPartIdentifier.Identifiers.Count == 1 && g.MultiPartIdentifier.Identifiers[0].Value == scalar.Key))
                            return this;

                        if (!partnames.ContainsKey(datePartType.GetColumnName()))
                            return this;
                    }
                }

                fetchXml.FetchXml.aggregate = true;
                fetchXml.FetchXml.aggregateSpecified = true;
                fetchXml.FetchXml = fetchXml.FetchXml;

                var schema = Source.GetSchema(metadata);

                foreach (var grouping in GroupBy)
                {
                    var colName = grouping.GetColumnName();
                    var alias = colName;
                    DateGroupingType? dateGrouping = null;

                    if (computeScalar != null && computeScalar.Columns.TryGetValue(colName, out var datePart))
                    {
                        dateGrouping = partnames[((ColumnReferenceExpression)((FunctionCall)datePart).Parameters[0]).GetColumnName()];
                        colName = ((ColumnReferenceExpression)((FunctionCall)datePart).Parameters[1]).GetColumnName();
                    }

                    schema.ContainsColumn(colName, out colName);

                    var attribute = AddAttribute(fetchXml, colName, a => a.groupby == FetchBoolType.@true && a.alias == alias, out _);
                    attribute.groupby = FetchBoolType.@true;
                    attribute.groupbySpecified = true;
                    attribute.alias = alias;

                    if (dateGrouping != null)
                    {
                        attribute.dategrouping = dateGrouping.Value;
                        attribute.dategroupingSpecified = true;
                    }
                }

                foreach (var agg in Aggregates)
                {
                    var col = (ColumnReferenceExpression)agg.Value.Expression;
                    var colName = col == null ? schema.PrimaryKey : col.GetColumnName();
                    schema.ContainsColumn(colName, out colName);

                    FetchXml.AggregateType aggregateType;

                    switch (agg.Value.AggregateType)
                    {
                        case ExecutionPlan.AggregateType.Average:
                            aggregateType = FetchXml.AggregateType.avg;
                            break;

                        case ExecutionPlan.AggregateType.Count:
                            aggregateType = FetchXml.AggregateType.countcolumn;
                            break;

                        case ExecutionPlan.AggregateType.CountStar:
                            aggregateType = FetchXml.AggregateType.count;
                            break;

                        case ExecutionPlan.AggregateType.Max:
                            aggregateType = FetchXml.AggregateType.max;
                            break;

                        case ExecutionPlan.AggregateType.Min:
                            aggregateType = FetchXml.AggregateType.min;
                            break;

                        case ExecutionPlan.AggregateType.Sum:
                            aggregateType = FetchXml.AggregateType.sum;
                            break;

                        default:
                            throw new ArgumentOutOfRangeException();
                    }

                    var attribute = AddAttribute(fetchXml, colName, a => a.aggregate == aggregateType && a.alias == agg.Key, out _);

                    attribute.aggregateSpecified = true;
                    attribute.alias = agg.Key;
                }

                return fetchXml;
            }

            return this;
        }
    }
}
