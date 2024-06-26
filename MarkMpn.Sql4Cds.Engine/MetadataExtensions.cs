﻿using System;
using System.Collections.Generic;
using System.Data.SqlTypes;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;

namespace MarkMpn.Sql4Cds.Engine
{
    static class MetadataExtensions
    {
        public static int EntityLogicalNameMaxLength { get; } = 64;

        public static string[] VirtualLookupAttributeSuffixes { get; } = new[] { "name", "type", "pid" };

        public static AttributeMetadata FindBaseAttributeFromVirtualAttribute(this EntityMetadata entity, string virtualAttributeLogicalName, out string suffix)
        {
            var matchingSuffix = VirtualLookupAttributeSuffixes.SingleOrDefault(s => virtualAttributeLogicalName.EndsWith(s, StringComparison.OrdinalIgnoreCase));
            suffix = matchingSuffix;

            if (suffix == null)
                return null;

            return entity.Attributes
                .SingleOrDefault(a => a.LogicalName.Equals(virtualAttributeLogicalName.Substring(0, virtualAttributeLogicalName.Length - matchingSuffix.Length), StringComparison.OrdinalIgnoreCase));
        }

        public static Type GetAttributeType(this AttributeMetadata attrMetadata)
        {
            if (attrMetadata is MultiSelectPicklistAttributeMetadata)
                return typeof(OptionSetValueCollection);

            var typeCode = attrMetadata.AttributeType;

            if (attrMetadata is ManagedPropertyAttributeMetadata managedProp)
                typeCode = managedProp.ValueAttributeTypeCode;

            if (attrMetadata is BooleanAttributeMetadata || typeCode == AttributeTypeCode.Boolean)
                return typeof(bool?);

            if (attrMetadata is DateTimeAttributeMetadata || typeCode == AttributeTypeCode.DateTime)
                return typeof(DateTime?);

            if (attrMetadata is DecimalAttributeMetadata || typeCode == AttributeTypeCode.Decimal)
                return typeof(decimal?);

            if (attrMetadata is DoubleAttributeMetadata || typeCode == AttributeTypeCode.Double)
                return typeof(double?);

            if (attrMetadata is EntityNameAttributeMetadata || typeCode == AttributeTypeCode.EntityName)
                return typeof(int?);

            if (attrMetadata is ImageAttributeMetadata)
                return typeof(byte[]);

            if (attrMetadata is IntegerAttributeMetadata || typeCode == AttributeTypeCode.Integer)
                return typeof(int?);

            if (attrMetadata is BigIntAttributeMetadata || typeCode == AttributeTypeCode.BigInt)
                return typeof(long?);

            if (typeCode == AttributeTypeCode.PartyList)
                return typeof(EntityCollection);

            if (attrMetadata is LookupAttributeMetadata || typeCode == AttributeTypeCode.Lookup || typeCode == AttributeTypeCode.Customer || typeCode == AttributeTypeCode.Owner)
                return typeof(Guid?);

            if (attrMetadata is MemoAttributeMetadata || typeCode == AttributeTypeCode.Memo)
                return typeof(string);

            if (attrMetadata is MoneyAttributeMetadata || typeCode == AttributeTypeCode.Money)
                return typeof(decimal?);

            if (attrMetadata is PicklistAttributeMetadata || typeCode == AttributeTypeCode.Picklist)
                return typeof(int?);

            if (attrMetadata is StateAttributeMetadata || typeCode == AttributeTypeCode.State)
                return typeof(int?);

            if (attrMetadata is StatusAttributeMetadata || typeCode == AttributeTypeCode.Status)
                return typeof(int?);

            if (attrMetadata is StringAttributeMetadata || typeCode == AttributeTypeCode.String)
                return typeof(string);

            if (attrMetadata is UniqueIdentifierAttributeMetadata || typeCode == AttributeTypeCode.Uniqueidentifier)
                return typeof(Guid?);

            if (attrMetadata.AttributeType == AttributeTypeCode.Virtual)
                return typeof(string);

            throw new ApplicationException("Unknown attribute type " + attrMetadata.GetType());
        }

        public static DataTypeReference GetAttributeSqlType(this AttributeMetadata attrMetadata, DataSource dataSource, bool write)
        {
            if (attrMetadata is MultiSelectPicklistAttributeMetadata)
                return DataTypeHelpers.NVarChar(Int32.MaxValue, dataSource.DefaultCollation, CollationLabel.Implicit);

            var typeCode = attrMetadata.AttributeType;

            if (attrMetadata is ManagedPropertyAttributeMetadata managedProp)
                typeCode = managedProp.ValueAttributeTypeCode;

            if (attrMetadata is BooleanAttributeMetadata || typeCode == AttributeTypeCode.Boolean)
                return DataTypeHelpers.Bit;

            if (attrMetadata is DateTimeAttributeMetadata || typeCode == AttributeTypeCode.DateTime)
                return DataTypeHelpers.DateTime;

            if (attrMetadata is DecimalAttributeMetadata || typeCode == AttributeTypeCode.Decimal)
            {
                short scale = 2;

                if (attrMetadata is DecimalAttributeMetadata dec && dec.Precision != null)
                    scale = (short)dec.Precision.Value; // Precision property is actually scale (number of decimal places)

                var precision = (short)(12 + scale); // Max value is 100 Billion, which is 12 digits
                
                return DataTypeHelpers.Decimal(precision, scale);
            }

            if (attrMetadata is DoubleAttributeMetadata || typeCode == AttributeTypeCode.Double)
                return DataTypeHelpers.Float;

            if (attrMetadata is EntityNameAttributeMetadata || typeCode == AttributeTypeCode.EntityName)
                return DataTypeHelpers.NVarChar(EntityLogicalNameMaxLength, dataSource.DefaultCollation, CollationLabel.Implicit);

            if (attrMetadata is ImageAttributeMetadata)
                return DataTypeHelpers.VarBinary(Int32.MaxValue);

            if (attrMetadata is IntegerAttributeMetadata || typeCode == AttributeTypeCode.Integer)
                return DataTypeHelpers.Int;

            if (attrMetadata is BigIntAttributeMetadata || typeCode == AttributeTypeCode.BigInt)
                return DataTypeHelpers.BigInt;

            if (typeCode == AttributeTypeCode.PartyList)
                return DataTypeHelpers.NVarChar(Int32.MaxValue, dataSource.DefaultCollation, CollationLabel.Implicit);

            if (attrMetadata is LookupAttributeMetadata || attrMetadata.IsPrimaryId == true || typeCode == AttributeTypeCode.Lookup || typeCode == AttributeTypeCode.Customer || typeCode == AttributeTypeCode.Owner)
                return DataTypeHelpers.EntityReference;

            if (attrMetadata is MemoAttributeMetadata || typeCode == AttributeTypeCode.Memo)
                return DataTypeHelpers.NVarChar(write && attrMetadata is MemoAttributeMetadata memo && memo.MaxLength != null ? memo.MaxLength.Value : Int32.MaxValue, dataSource.DefaultCollation, CollationLabel.CoercibleDefault);
            
            if (attrMetadata is MoneyAttributeMetadata || typeCode == AttributeTypeCode.Money)
                return DataTypeHelpers.Money;

            if (attrMetadata is PicklistAttributeMetadata || typeCode == AttributeTypeCode.Picklist)
                return DataTypeHelpers.Int;

            if (attrMetadata is StateAttributeMetadata || typeCode == AttributeTypeCode.State)
                return DataTypeHelpers.Int;

            if (attrMetadata is StatusAttributeMetadata || typeCode == AttributeTypeCode.Status)
                return DataTypeHelpers.Int;

            if (attrMetadata is StringAttributeMetadata || typeCode == AttributeTypeCode.String)
            {
                if (attrMetadata.LogicalName.StartsWith("address"))
                {
                    var parts = attrMetadata.LogicalName.Split('_');
                    if (parts.Length == 2 && Int32.TryParse(parts[0].Substring(7), out _) && dataSource.Metadata.TryGetValue("customeraddress", out var addressMetadata))
                    {
                        // Attribute is e.g. address1_postalcode. Get the equivalent attribute from the customeraddress
                        // entity as it can have very different max length
                        attrMetadata = addressMetadata.Attributes.SingleOrDefault(a => a.LogicalName == parts[1]) as StringAttributeMetadata ?? attrMetadata;
                    }
                }

                var maxLength = Int32.MaxValue;

                if (attrMetadata is StringAttributeMetadata str)
                {
                    // MaxLength validation is applied on write, but existing values could be up to DatabaseLength / 2
                    maxLength = str.MaxLength ?? maxLength;

                    if (!write && str.DatabaseLength != null && str.DatabaseLength.Value / 2 > maxLength)
                        maxLength = str.DatabaseLength.Value / 2;
                }

                return DataTypeHelpers.NVarChar(maxLength, dataSource.DefaultCollation, CollationLabel.Implicit);
            }

            if (attrMetadata is UniqueIdentifierAttributeMetadata || typeCode == AttributeTypeCode.Uniqueidentifier)
                return DataTypeHelpers.UniqueIdentifier;

            if (attrMetadata.AttributeTypeName == AttributeTypeDisplayName.FileType)
                return DataTypeHelpers.UniqueIdentifier;

            if (attrMetadata.AttributeType == AttributeTypeCode.Virtual)
                return DataTypeHelpers.NVarChar(Int32.MaxValue, dataSource.DefaultCollation, CollationLabel.Implicit);

            throw new ApplicationException("Unknown attribute type " + attrMetadata.GetType());
        }
    }
}
