// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Specialized;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.DataApiBuilder.Core.Configurations;
using Azure.DataApiBuilder.Core.Models;
using Azure.DataApiBuilder.Core.Services;
using Azure.DataApiBuilder.Service.Exceptions;
using Azure.DataApiBuilder.Service.GraphQLBuilder.GraphQLTypes;
using Azure.DataApiBuilder.Service.GraphQLBuilder.Queries;
using Microsoft.AspNetCore.WebUtilities;

namespace Azure.DataApiBuilder.Core.Resolvers
{
    /// <summary>
    /// Contains methods to help generating the *Connection result for pagination
    /// </summary>
    public static class SqlPaginationUtil
    {
        /// <summary>
        /// Receives the result of a query as a JsonElement and parses:
        /// <list type="bullet">
        /// <list>*Connection.items which is trivially resolved to all the elements of the result (last  discarded if hasNextPage has been requested)</list>
        /// <list>*Connection.endCursor which is the primary key of the last element of the result (last  discarded if hasNextPage has been requested)</list>
        /// <list>*Connection.hasNextPage which is decided on whether structure.Limit() elements have been returned</list>
        /// </list>
        /// </summary>
        public static JsonDocument CreatePaginationConnectionFromJsonElement(JsonElement root, PaginationMetadata paginationMetadata)
        {
            // Maintains the connection JSON object *Connection
            Dictionary<string, object> connectionJson = new();

            // in dw we wrap array with "" and hence jsonValueKind is string instead of array.
            if (root.ValueKind is JsonValueKind.String)
            {
                JsonDocument document = JsonDocument.Parse(root.GetString()!);
                root = document.RootElement;
            }

            IEnumerable<JsonElement> rootEnumerated = root.EnumerateArray();

            bool hasExtraElement = false;
            if (paginationMetadata.RequestedHasNextPage)
            {
                // check if the number of elements requested is successfully returned
                // structure.Limit() is first + 1 for paginated queries where hasNextPage is requested
                hasExtraElement = rootEnumerated.Count() == paginationMetadata.Structure!.Limit();

                // add hasNextPage to connection elements
                connectionJson.Add(QueryBuilder.HAS_NEXT_PAGE_FIELD_NAME, hasExtraElement ? true : false);

                if (hasExtraElement)
                {
                    // remove the last element
                    rootEnumerated = rootEnumerated.Take(rootEnumerated.Count() - 1);
                }
            }

            int returnedElemNo = rootEnumerated.Count();

            if (paginationMetadata.RequestedItems)
            {
                if (hasExtraElement)
                {
                    // use rootEnumerated to make the *Connection.items since the last element of rootEnumerated
                    // is removed if the result has an extra element
                    connectionJson.Add(QueryBuilder.PAGINATION_FIELD_NAME, JsonSerializer.Serialize(rootEnumerated.ToArray()));
                }
                else
                {
                    // if the result doesn't have an extra element, just return the dbResult for *Connection.items
                    connectionJson.Add(QueryBuilder.PAGINATION_FIELD_NAME, root.ToString()!);
                }
            }

            if (paginationMetadata.RequestedEndCursor)
            {
                // parse *Connection.endCursor if there are no elements
                // if no after is added, but it has been requested HotChocolate will report it as null
                if (returnedElemNo > 0)
                {
                    JsonElement lastElemInRoot = rootEnumerated.ElementAtOrDefault(returnedElemNo - 1);
                    connectionJson.Add(QueryBuilder.PAGINATION_TOKEN_FIELD_NAME,
                        MakeCursorFromJsonElement(
                            lastElemInRoot,
                            paginationMetadata.Structure!.PrimaryKey(),
                            paginationMetadata.Structure!.OrderByColumns,
                            paginationMetadata.Structure!.EntityName,
                            paginationMetadata.Structure!.DatabaseObject.SchemaName,
                            paginationMetadata.Structure!.DatabaseObject.Name,
                            paginationMetadata.Structure!.MetadataProvider));
                }
            }

            return JsonDocument.Parse(JsonSerializer.Serialize(connectionJson));
        }

        /// <summary>
        /// Wrapper for CreatePaginationConnectionFromJsonElement
        /// Disposes the JsonDocument passed to it
        /// <summary>
        public static JsonDocument CreatePaginationConnectionFromJsonDocument(JsonDocument? jsonDocument, PaginationMetadata paginationMetadata)
        {
            // necessary for MsSql because it doesn't coalesce list query results like Postgres
            if (jsonDocument is null)
            {
                jsonDocument = JsonDocument.Parse("[]");
            }

            JsonElement root = jsonDocument.RootElement;

            // this is intentionally not disposed since it will be used for processing later
            JsonDocument result = CreatePaginationConnectionFromJsonElement(root, paginationMetadata);

            // no longer needed, so it is disposed
            jsonDocument.Dispose();

            return result;
        }

        /// <summary>
        /// Holds the information safe to expose in the response's pagination cursor,
        /// the NextLink. The NextLink column represents the safe to expose information
        /// that defines the entity, field, field value, and direction of sorting to
        /// continue to the next page. These can then be used to form the pagination
        /// columns that will be needed for the actual query.
        /// </summary>
        protected class NextLinkField
        {
            public string EntityName { get; set; }
            public string FieldName { get; set; }
            public object? FieldValue { get; }
            public string? ParamName { get; set; }
            public OrderBy Direction { get; set; }

            public NextLinkField(
                string entityName,
                string fieldName,
                object? fieldValue,
                string? paramName = null,
                // default sorting direction is ascending so we maintain that convention
                OrderBy direction = OrderBy.ASC)
            {
                EntityName = entityName;
                FieldName = fieldName;
                FieldValue = fieldValue;
                ParamName = paramName;
                Direction = direction;
            }
        }

        /// <summary>
        /// Extracts the columns from the JsonElement needed for pagination, represents them as a string in json format and base64 encodes.
        /// The JSON is encoded in base64 for opaqueness. The cursor should function as a token that the user copies and pastes
        /// without needing to understand how it works.
        /// </summary>
        public static string MakeCursorFromJsonElement(
            JsonElement element,
            List<string> primaryKey,
            List<OrderByColumn>? orderByColumns,
            string entityName = "",
            string schemaName = "",
            string tableName = "",
            ISqlMetadataProvider? sqlMetadataProvider = null)
        {
            List<NextLinkField> cursorJson = new();
            JsonSerializerOptions options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
            // Hash set is used here to maintain linear runtime
            // in the worst case for this function. If list is used
            // we will have in the worst case quadratic runtime.
            HashSet<string> remainingKeys = new();
            foreach (string key in primaryKey)
            {
                remainingKeys.Add(key);
            }

            // must include all orderByColumns to maintain
            // correct pagination with sorting
            if (orderByColumns is not null)
            {
                foreach (OrderByColumn column in orderByColumns)
                {
                    string? exposedColumnName = GetExposedColumnName(entityName, column.ColumnName, sqlMetadataProvider);
                    if (TryResolveJsonElementToScalarVariable(element.GetProperty(exposedColumnName), out object? value))
                    {
                        cursorJson.Add(new NextLinkField(
                            entityName: entityName,
                            fieldName: exposedColumnName,
                            fieldValue: value,
                            direction: column.Direction));
                    }
                    else
                    {
                        throw new DataApiBuilderException(
                            message: "Incompatible data to create pagination cursor.",
                            statusCode: HttpStatusCode.BadRequest,
                            subStatusCode: DataApiBuilderException.SubStatusCodes.ErrorProcessingData);
                    }

                    remainingKeys.Remove(column.ColumnName);
                }
            }

            // Primary key columns must be included in the orderBy query parameter in the nextLink cursor to break ties between result set records.
            // Iterate through list of (composite) primary key(s) and when a primary key column exists in the remaining keys collection:
            // 1.) Add that column as one of the pagination columns in the orderBy query parameter in the generated nextLink cursor.
            // 2.) Remove the column from the remaining keys collection.
            // This loop enables consistent iteration over the list of primary key columns which:
            // - Maintains the order of the primary key columns as they exist in the database.
            // - Ensures all primary key columns have been added to the nextLink cursor.
            foreach (string column in primaryKey)
            {
                if (remainingKeys.Contains(column))
                {
                    string? exposedColumnName = GetExposedColumnName(entityName, column, sqlMetadataProvider);
                    if (TryResolveJsonElementToScalarVariable(element.GetProperty(exposedColumnName), out object? value))
                    {
                        cursorJson.Add(new NextLinkField(
                            entityName: entityName,
                            fieldName: exposedColumnName,
                            fieldValue: value));
                    }
                    else
                    {
                        throw new DataApiBuilderException(
                           message: "Incompatible data to create pagination cursor.",
                           statusCode: HttpStatusCode.BadRequest,
                           subStatusCode: DataApiBuilderException.SubStatusCodes.ErrorProcessingData);
                    }

                    remainingKeys.Remove(column);
                }
            }

            return Base64Encode(JsonSerializer.Serialize(cursorJson, options));
        }

        /// <summary>
        /// Parse the value of "after" parameter from query parameters, validate it, and return the json object it stores
        /// </summary>
        public static IEnumerable<PaginationColumn> ParseAfterFromQueryParams(
            IDictionary<string, object?> queryParams,
            PaginationMetadata paginationMetadata,
            ISqlMetadataProvider sqlMetadataProvider,
            string EntityName,
            RuntimeConfigProvider runtimeConfigProvider)
        {
            if (queryParams.TryGetValue(QueryBuilder.PAGINATION_TOKEN_ARGUMENT_NAME, out object? continuationObject))
            {
                if (continuationObject is not null)
                {
                    string afterPlainText = (string)continuationObject;
                    return ParseAfterFromJsonString(
                        afterPlainText,
                        paginationMetadata,
                        sqlMetadataProvider,
                        EntityName,
                        runtimeConfigProvider);
                }
            }

            return Enumerable.Empty<PaginationColumn>();
        }

        /// <summary>
        /// Validate the value associated with $after, and return list of orderby columns
        /// it represents.
        /// </summary>
        public static IEnumerable<PaginationColumn> ParseAfterFromJsonString(
            string afterJsonString,
            PaginationMetadata paginationMetadata,
            ISqlMetadataProvider sqlMetadataProvider,
            string entityName,
            RuntimeConfigProvider runtimeConfigProvider
            )
        {
            List<PaginationColumn>? paginationCursorColumnsForQuery = new();
            IEnumerable<NextLinkField>? paginationCursorFieldsFromRequest;
            try
            {
                afterJsonString = Base64Decode(afterJsonString);
                paginationCursorFieldsFromRequest = JsonSerializer.Deserialize<IEnumerable<NextLinkField>>(afterJsonString);

                if (paginationCursorFieldsFromRequest is null)
                {
                    throw new ArgumentException("Failed to parse the pagination information from the provided token");
                }

                Dictionary<string, PaginationColumn> exposedFieldNameToBackingColumn = new();
                foreach (NextLinkField field in paginationCursorFieldsFromRequest)
                {
                    // REST calls this function with a non null sqlMetadataProvider
                    // which will get the exposed name for safe messaging in the response.
                    // Since we are looking for pagination columns from the $after query
                    // param, we expect this column to exist as the $after query param
                    // was formed from a previous response with a nextLink. If the nextLink
                    // has been modified and backingColumn is null we throw exception.
                    string backingColumnName = GetBackingColumnName(entityName, field.FieldName, sqlMetadataProvider);
                    if (backingColumnName is null)
                    {
                        throw new DataApiBuilderException(
                            message: $"Pagination token is not well formed because {field.FieldName} is not valid.",
                            statusCode: HttpStatusCode.BadRequest,
                            subStatusCode: DataApiBuilderException.SubStatusCodes.BadRequest);
                    }

                    PaginationColumn pageColumn = new(
                        tableName: "",
                        tableSchema: "",
                        columnName: backingColumnName,
                        value: field.FieldValue,
                        paramName: field.ParamName,
                        direction: field.Direction);
                    paginationCursorColumnsForQuery.Add(pageColumn);
                    // holds exposed name mapped to exposed pagination column
                    exposedFieldNameToBackingColumn.Add(field.FieldName, pageColumn);
                }

                // verify that primary keys is a sub set of after's column names
                // if any primary keys are not contained in after's column names we throw exception
                List<string> primaryKeys = paginationMetadata.Structure!.PrimaryKey();

                foreach (string pk in primaryKeys)
                {
                    // REST calls this function with a non null sqlMetadataProvider
                    // which will get the exposed name for safe messaging in the response.
                    // Since we are looking for primary keys we expect these columns to
                    // exist.
                    string exposedFieldName = GetExposedColumnName(entityName, pk, sqlMetadataProvider);
                    if (!exposedFieldNameToBackingColumn.ContainsKey(exposedFieldName))
                    {
                        throw new DataApiBuilderException(
                            message: $"Pagination token is not well formed because it is missing an expected field: {exposedFieldName}",
                            statusCode: HttpStatusCode.BadRequest,
                            subStatusCode: DataApiBuilderException.SubStatusCodes.BadRequest);
                    }
                }

                // verify that orderby columns for the structure and the after columns
                // match in name and direction
                int orderByColumnCount = 0;
                SqlQueryStructure structure = paginationMetadata.Structure!;
                foreach (OrderByColumn column in structure.OrderByColumns)
                {
                    string exposedFieldName = GetExposedColumnName(entityName, column.ColumnName, sqlMetadataProvider);

                    if (!exposedFieldNameToBackingColumn.ContainsKey(exposedFieldName) ||
                        exposedFieldNameToBackingColumn[exposedFieldName].Direction != column.Direction)
                    {
                        // REST calls this function with a non null sqlMetadataProvider
                        // which will get the exposed name for safe messaging in the response.
                        // Since we are looking for valid orderby columns we expect
                        // these columns to exist.
                        string exposedOrderByFieldName = GetExposedColumnName(entityName, column.ColumnName, sqlMetadataProvider);
                        throw new DataApiBuilderException(
                            message: $"Could not match order by column {exposedOrderByFieldName} with a column in the pagination token with the same name and direction.",
                            statusCode: HttpStatusCode.BadRequest,
                            subStatusCode: DataApiBuilderException.SubStatusCodes.BadRequest);
                    }

                    orderByColumnCount++;
                }

                // the check above validates that all orderby columns are matched with after columns
                // also validate that there are no extra after columns
                if (exposedFieldNameToBackingColumn.Count != orderByColumnCount)
                {
                    throw new ArgumentException("After token contains extra columns not present in order by columns.");
                }
            }
            catch (Exception e) when (
                e is InvalidCastException ||
                e is ArgumentException ||
                e is ArgumentNullException ||
                e is FormatException ||
                e is System.Text.DecoderFallbackException ||
                e is JsonException ||
                e is NotSupportedException
                )
            {
                // Possible sources of exceptions:
                // stringObject cannot be converted to string
                // afterPlainText cannot be successfully decoded
                // afterJsonString cannot be deserialized
                // keys of afterDeserialized do not correspond to the primary key
                // values given for the primary keys are of incorrect format
                // duplicate column names in the after token and / or the orderby columns
                string errorMessage = runtimeConfigProvider.GetConfig().IsDevelopmentMode() ? $"{e.Message}\n{e.StackTrace}" :
                    $"{afterJsonString} is not a valid pagination token.";
                throw new DataApiBuilderException(
                    message: errorMessage,
                    statusCode: HttpStatusCode.BadRequest,
                    subStatusCode: DataApiBuilderException.SubStatusCodes.BadRequest,
                    innerException: e);
            }

            return paginationCursorColumnsForQuery;
        }

        /// <summary>
        /// Helper function will return the backing column name, which is
        /// what is used to form pagination columns in the query.
        /// </summary>
        /// <param name="entityName">String holds the name of the entity.</param>
        /// <param name="exposedColumnName">String holds the name of the exposed column.</param>
        /// <param name="sqlMetadataProvider">Holds the sqlmetadataprovider for REST requests,
        /// which provides mechanisms to resolve exposedName -> backingColumnName and
        /// backingColumnName -> exposedName.</param>
        /// <returns>the backing column name.</returns>
        /// <returns></returns>
        private static string GetBackingColumnName(string entityName, string exposedColumnName, ISqlMetadataProvider? sqlMetadataProvider)
        {
            if (sqlMetadataProvider is not null)
            {
                sqlMetadataProvider.TryGetBackingColumn(entityName, exposedColumnName, out exposedColumnName!);
            }

            return exposedColumnName;
        }

        /// <summary>
        /// Helper function will return the exposed column name, which is
        /// what is used to return a cursor in the response, since we only
        /// use the exposed names in requests and responses.
        /// </summary>
        /// <param name="entityName">String holds the name of the entity.</param>
        /// <param name="backingColumn">String holds the name of the backing column.</param>
        /// <param name="sqlMetadataProvider">Holds the sqlmetadataprovider for REST requests.</param>
        /// <returns>the exposed name</returns>
        private static string GetExposedColumnName(string entityName, string backingColumn, ISqlMetadataProvider? sqlMetadataProvider)
        {
            if (sqlMetadataProvider is not null)
            {
                sqlMetadataProvider.TryGetExposedColumnName(entityName, backingColumn, out backingColumn!);
            }

            return backingColumn;
        }

        /// <summary>
        /// Tries to resolve a JsonElement representing a variable to the appropriate type
        /// </summary>
        /// <param name="element">The Json element to convert from.</param>
        /// <param name="scalarVariable">The scalar into which the element is resolved based on its ValueKind.</param>
        /// <returns>True when resolution is successful, false otherwise.</returns>
        public static bool TryResolveJsonElementToScalarVariable(
            JsonElement element,
            out object? scalarVariable)
        {
            bool resolved = true;
            scalarVariable = null;
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                {
                    scalarVariable = element.GetString();
                    break;
                }

                case JsonValueKind.Number:
                {
                    if (element.TryGetDouble(out double value))
                    {
                        scalarVariable = value;
                    }

                    break;
                }

                case JsonValueKind.Null:
                {
                    scalarVariable = null;
                    break;
                }

                case JsonValueKind.True:
                {
                    scalarVariable = true;
                    break;
                }

                case JsonValueKind.False:
                {
                    scalarVariable = false;
                    break;
                }

                default:
                {
                    resolved = false;
                    break;
                }
            }

            return resolved;
        }

        /// <summary>
        /// Encodes string to base64
        /// </summary>
        public static string Base64Encode(string plainText)
        {
            byte[] plainTextBytes = System.Text.Encoding.UTF8.GetBytes(plainText);
            return Convert.ToBase64String(plainTextBytes);
        }

        /// <summary>
        /// Decode base64 string to plain text
        /// </summary>
        public static string Base64Decode(string base64EncodedData)
        {
            byte[] base64EncodedBytes = Convert.FromBase64String(base64EncodedData);
            return System.Text.Encoding.UTF8.GetString(base64EncodedBytes);
        }

        /// <summary>
        /// Create the URL that will provide for the next page of results
        /// using the same query options.
        /// </summary>
        /// <param name="path">The request path.</param>
        /// <param name="nvc">Collection of query params.</param>
        /// <param name="after">The values needed for next page.</param>
        /// <returns>The string representing nextLink.</returns>
        public static JsonElement CreateNextLink(string path, NameValueCollection? nvc, string after)
        {
            if (nvc is null)
            {
                nvc = new();
            }

            if (!string.IsNullOrWhiteSpace(after))
            {
                nvc["$after"] = after;
            }

            string queryString = FormatQueryString(queryParameters: nvc);

            // ValueKind will be array so we can differentiate from other objects in the response
            // to be returned.
            string jsonString = JsonSerializer.Serialize(new[]
            {
                new
                {
                    nextLink = @$"{path}{queryString}"
                }
            });
            return JsonSerializer.Deserialize<JsonElement>(jsonString);
        }

        /// <summary>
        /// Returns true if the table has more records that
        /// match the query options than were requested.
        /// </summary>
        /// <param name="jsonResult">Results plus one extra record if more exist.</param>
        /// <param name="first">Client provided limit if one exists, otherwise 0.</param>
        /// <returns>Bool representing if more records are available.</returns>
        public static bool HasNext(JsonElement jsonResult, uint? first)
        {
            // When first is 0 we use default limit of 100, otherwise we use first
            uint numRecords = (uint)jsonResult.GetArrayLength();
            uint? limit = first is not null ? first : 100;
            return numRecords > limit;
        }

        /// <summary>
        /// Creates a query string from a NameValueCollection using .NET QueryHelpers.
        /// Addresses the limitations:
        /// 1) NameValueCollection is not resolved as string in JSON serialization.
        /// 2) NameValueCollection keys and values are not URL escaped.
        /// </summary>
        /// <param name="queryParameters">Key: $QueryParamKey Value: QueryParamValue</param>
        /// <returns>Query string prefixed with question mark (?). Returns an empty string when
        /// no entries exist in queryParameters.</returns>
        public static string FormatQueryString(NameValueCollection queryParameters)
        {
            string queryString = "";
            foreach (string key in queryParameters)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                // There may be duplicate query parameter keys, so get
                // all values associated to given key in a comma-separated list
                // format compatible with OData expression syntax.
                string? queryParamValues = queryParameters.Get(key);

                if (!string.IsNullOrWhiteSpace(queryParamValues))
                {
                    queryString = QueryHelpers.AddQueryString(queryString, key, queryParamValues);
                }
            }

            return queryString;
        }
    }
}
