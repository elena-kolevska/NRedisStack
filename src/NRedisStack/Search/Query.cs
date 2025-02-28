﻿using System.Globalization;
using NRedisStack.Search.Literals;
using StackExchange.Redis;

namespace NRedisStack.Search
{
    /// <summary>
    ///  Query represents query parameters and filters to load results from the engine
    /// </summary>
    public sealed class Query
    {
        /// <summary>
        /// Filter represents a filtering rules in a query
        /// </summary>
        public abstract class Filter
        {
            public string Property { get; }

            internal abstract void SerializeRedisArgs(List<object> args);

            internal Filter(string property)
            {
                Property = property;
            }
        }

        /// <summary>
        /// NumericFilter wraps a range filter on a numeric field. It can be inclusive or exclusive
        /// </summary>
        public class NumericFilter : Filter
        {
            private readonly double min, max;
            private readonly bool exclusiveMin, exclusiveMax;

            public NumericFilter(string property, double min, bool exclusiveMin, double max, bool exclusiveMax) : base(property)
            {
                this.min = min;
                this.max = max;
                this.exclusiveMax = exclusiveMax;
                this.exclusiveMin = exclusiveMin;
            }

            public NumericFilter(string property, double min, double max) : this(property, min, false, max, false) { }

            internal override void SerializeRedisArgs(List<object> args)
            {
                static RedisValue FormatNum(double num, bool exclude)
                {
                    if (!exclude || double.IsInfinity(num))
                    {
                        return (RedisValue)num; // can use directly
                    }
                    // need to add leading bracket
                    return "(" + num.ToString("G17", NumberFormatInfo.InvariantInfo);
                }
                args.Add(SearchArgs.FILTER);
                args.Add(Property);
                args.Add(FormatNum(min, exclusiveMin));
                args.Add(FormatNum(max, exclusiveMax));
            }
        }

        /// <summary>
        /// GeoFilter encapsulates a radius filter on a geographical indexed fields
        /// </summary>
        public class GeoFilter : Filter
        {
            public static readonly string KILOMETERS = "km";
            public static readonly string METERS = "m";
            public static readonly string FEET = "ft";
            public static readonly string MILES = "mi";
            private readonly double lon, lat, radius;
            private readonly string unit; // TODO: think about implementing this as an enum

            public GeoFilter(string property, double lon, double lat, double radius, string unit) : base(property)
            {
                this.lon = lon;
                this.lat = lat;
                this.radius = radius;
                this.unit = unit;
            }

            internal override void SerializeRedisArgs(List<object> args)
            {
                args.Add("GEOFILTER");
                args.Add(Property);
                args.Add(lon);
                args.Add(lat);
                args.Add(radius);
                args.Add(unit);
            }
        }

        internal readonly struct Paging
        {
            public int Offset { get; }
            public int Count { get; }

            public Paging(int offset, int count)
            {
                Offset = offset;
                Count = count;
            }
        }

        public readonly struct HighlightTags
        {
            public HighlightTags(string open, string close)
            {
                Open = open;
                Close = close;
            }
            public string Open { get; }
            public string Close { get; }
        }

        /// <summary>
        /// The query's filter list. We only support AND operation on all those filters
        /// </summary>
        internal readonly List<Filter> _filters = new List<Filter>();

        /// <summary>
        /// The textual part of the query
        /// </summary>
        public string QueryString { get; }

        /// <summary>
        /// The sorting parameters
        /// </summary>
        internal Paging _paging = new Paging(0, 10);

        /// <summary>
        /// Set the query to verbatim mode, disabling stemming and query expansion
        /// </summary>
        public bool Verbatim { get; set; }
        /// <summary>
        /// Set the query not to return the contents of documents, and rather just return the ids
        /// </summary>
        public bool NoContent { get; set; }
        /// <summary>
        /// Set the query not to filter for stopwords. In general this should not be used
        /// </summary>
        public bool NoStopwords { get; set; }
        /// <summary>
        /// Set the query to return a factored score for each results. This is useful to merge results from multiple queries.
        /// </summary>
        public bool WithScores { get; set; }
        /// <summary>
        /// Set the query to return object payloads, if any were given
        /// </summary>
        public bool WithPayloads { get; set; }

        /// <summary>
        /// Set the query language, for stemming purposes; see http://redisearch.io for documentation on languages and stemming
        /// </summary>
        public string Language { get; set; }

        internal string[] _fields = null;
        internal string[] _keys = null;
        internal string[] _returnFields = null;
        internal FieldName[] _returnFieldsNames = null;
        internal string[] _highlightFields = null;
        internal string[] _summarizeFields = null;
        internal HighlightTags? _highlightTags = null;
        internal string _summarizeSeparator = null;
        internal int _summarizeNumFragments = -1, _summarizeFragmentLen = -1;

        /// <summary>
        /// Set the query payload to be evaluated by the scoring function
        /// </summary>
        public string Payload { get; set; } // TODO: should this be a byte[]?

        // TODO: Check if I need to add here WITHSORTKEYS

        /// <summary>
        /// Set the query parameter to sort by
        /// </summary>
        public string SortBy { get; set; }

        /// <summary>
        /// Set the query parameter to sort by ASC by default
        /// </summary>
        public bool? SortAscending { get; set; } = null;

        // highlight and summarize
        internal bool _wantsHighlight = false, _wantsSummarize = false;

        /// <summary>
        /// Set the query scoring. see https://oss.redislabs.com/redisearch/Scoring.html for documentation
        /// </summary>
        public string Scorer { get; set; }
        // public bool ExplainScore { get; set; } // TODO: Check if this is needed because Jedis doesn't have it

        private Dictionary<string, object> _params = new Dictionary<string, object>();
        public int? dialect { get; private set;} = null;
        private int _slop = -1;
        private long _timeout = -1;
        private bool _inOrder = false;
        private string? _expander = null;

        public Query() : this("*") { }

        /// <summary>
        /// Create a new index
        /// </summary>
        /// <param name="queryString">The query string to use for this query.</param>
        public Query(string queryString)
        {
            QueryString = queryString;
        }

        internal void SerializeRedisArgs(List<object> args)
        {
            args.Add(QueryString);

            if (Verbatim)
            {
                args.Add("VERBATIM");
            }
            if (NoContent)
            {
                args.Add("NOCONTENT");
            }
            if (NoStopwords)
            {
                args.Add("NOSTOPWORDS");
            }
            if (WithScores)
            {
                args.Add("WITHSCORES");
                // if (ExplainScore)
                // {
                //     args.Add("EXPLAINSCORE"); // TODO: Check Why Jedis doesn't have it
                // }
            }
            if (WithPayloads)
            {
                args.Add("WITHPAYLOADS");
            }
            if (Language != null)
            {
                args.Add("LANGUAGE");
                args.Add(Language);
            }

            if (Scorer != null)
            {
                args.Add("SCORER");
                args.Add(Scorer);
            }

            if (_fields?.Length > 0)
            {
                args.Add("INFIELDS");
                args.Add(_fields.Length);
                args.AddRange(_fields);
            }

            if (SortBy != null)
            {
                args.Add("SORTBY");
                args.Add(SortBy);
                if (SortAscending != null)
                    args.Add(((bool)SortAscending ? "ASC" : "DESC"));
            }
            if (Payload != null)
            {
                args.Add("PAYLOAD");
                args.Add(Payload);
            }

            if (_paging.Offset != 0 || _paging.Count != 10)
            {
                args.Add("LIMIT");
                args.Add(_paging.Offset);
                args.Add(_paging.Count);
            }

            if (_filters?.Count > 0)
            {
                foreach (var f in _filters)
                {
                    f.SerializeRedisArgs(args);
                }
            }

            if (_wantsHighlight)
            {
                args.Add("HIGHLIGHT");
                if (_highlightFields != null)
                {
                    args.Add("FIELDS");
                    args.Add(_highlightFields.Length);
                    foreach (var s in _highlightFields)
                    {
                        args.Add(s);
                    }
                }
                if (_highlightTags != null)
                {
                    args.Add("TAGS");
                    var tags = _highlightTags.GetValueOrDefault();
                    args.Add(tags.Open);
                    args.Add(tags.Close);
                }
            }
            if (_wantsSummarize)
            {
                args.Add("SUMMARIZE");
                if (_summarizeFields != null)
                {
                    args.Add("FIELDS");
                    args.Add(_summarizeFields.Length);
                    foreach (var s in _summarizeFields)
                    {
                        args.Add(s);
                    }
                }
                if (_summarizeNumFragments != -1)
                {
                    args.Add("FRAGS");
                    args.Add(_summarizeNumFragments);
                }
                if (_summarizeFragmentLen != -1)
                {
                    args.Add("LEN");
                    args.Add(_summarizeFragmentLen);
                }
                if (_summarizeSeparator != null)
                {
                    args.Add("SEPARATOR");
                    args.Add(_summarizeSeparator);
                }
            }

            if (_keys != null && _keys.Length > 0)
            {
                args.Add("INKEYS");
                args.Add(_keys.Length);

                foreach (var key in _keys)
                {
                    args.Add(key);
                }
            }

            if (_returnFields?.Length > 0)
            {
                args.Add("RETURN");
                args.Add(_returnFields.Length);
                args.AddRange(_returnFields);
            }

            else if (_returnFieldsNames?.Length > 0)
            {
                args.Add("RETURN");
                int returnCountIndex = args.Count;
                int returnCount = 0;
                foreach (FieldName fn in _returnFieldsNames)
                {
                    returnCount += fn.AddCommandArguments(args);
                }

                args.Insert(returnCountIndex, returnCount);
            }
            if (_params != null && _params.Count > 0)
            {
                args.Add("PARAMS");
                args.Add(_params.Count * 2);
                foreach (var entry in _params)
                {
                    args.Add(entry.Key);
                    args.Add(entry.Value);
                }
            }

            if (dialect >= 1)
            {
                args.Add("DIALECT");
                args.Add(dialect);
            }

            if (_slop >= 0)
            {
                args.Add("SLOP");
                args.Add(_slop);
            }

            if (_timeout >= 0)
            {
                args.Add("TIMEOUT");
                args.Add(_timeout);
            }

            if (_inOrder)
            {
                args.Add("INORDER");
            }

            if (_expander != null)
            {
                args.Add("EXPANDER");
                args.Add(_expander);
            }
        }

        // TODO: check if DelayedRawable is needed here (Jedis have it)

        /// <summary>
        /// Limit the results to a certain offset and limit
        /// </summary>
        /// <param name="offset">the first result to show, zero based indexing</param>
        /// <param name="count">how many results we want to show</param>
        /// <returns>the query itself, for builder-style syntax</returns>
        public Query Limit(int offset, int count)
        {
            _paging = new Paging(offset, count);
            return this;
        }

        /// <summary>
        /// Add a filter to the query's filter list
        /// </summary>
        /// <param name="f">either a numeric or geo filter object</param>
        /// <returns>the query itself</returns>
        public Query AddFilter(Filter f)
        {
            _filters.Add(f);
            return this;
        }

        /// <summary>
        /// Set the query payload to be evaluated by the scoring function
        /// </summary>
        /// <param name="payload">the payload</param>
        /// <returns>the query itself</returns>
        public Query SetPayload(string payload)
        {
            Payload = payload;
            return this;
        }

        /// <summary>
        /// Set the query to verbatim mode, disabling stemming and query expansion
        /// </summary>
        /// <returns>the query itself</returns>
        public Query SetVerbatim(bool value = true)
        {
            Verbatim = value;
            return this;
        }

        /// <summary>
        /// Set the query not to return the contents of documents, and rather just return the ids
        /// </summary>
        /// <returns>the query itself</returns>
        public Query SetNoContent(bool value = true)
        {
            NoContent = value;
            return this;
        }

        /// <summary>
        /// Set the query not to filter for stopwords. In general this should not be used
        /// </summary>
        /// <returns>the query itself</returns>
        public Query SetNoStopwords(bool value = true)
        {
            NoStopwords = value;
            return this;
        }

        /// <summary>
        /// Set the query to return a factored score for each results. This is useful to merge results from
        /// multiple queries.
        /// </summary>
        /// <returns>the query itself</returns>
        public Query SetWithScores(bool value = true)
        {
            WithScores = value;
            return this;
        }

        /// <summary>
        /// Set the query to return object payloads, if any were given
        /// </summary>
        /// <returns>the query itself</returns>
        public Query SetWithPayloads()
        {
            WithPayloads = true;
            return this;
        }

        /// <summary>
        /// Set the query language, for stemming purposes
        /// </summary>
        /// <param name="language">the language</param>
        /// <returns>the query itself</returns>
        public Query SetLanguage(string language)
        {
            Language = language;
            return this;
        }

        /// <summary>
        /// Set the query language, for stemming purposes
        /// </summary>
        /// <param name="scorer"></param>
        /// <returns></returns>
        public Query SetScorer(string scorer)
        {
            Scorer = scorer;
            return this;
        }

        // TODO: check if this is needed (Jedis doesn't have it)
        // /// <summary>
        // /// returns a textual description of how the scores were calculated.
        // /// Using this options requires the WITHSCORES option.
        // /// </summary>
        // /// <param name="explainScore"></param>
        // /// <returns></returns>
        // public Query SetExplainScore(bool explainScore = true)
        // {
        //     ExplainScore = explainScore;
        //     return this;
        // }

        /// <summary>
        /// Limit the query to results that are limited to a specific set of fields
        /// </summary>
        /// <param name="fields">a list of TEXT fields in the schemas</param>
        /// <returns>the query object itself</returns>
        public Query LimitFields(params string[] fields)
        {
            _fields = fields;
            return this;
        }

        /// <summary>
        /// Limit the query to results that are limited to a specific set of keys
        /// </summary>
        /// <param name="keys">a list of the TEXT fields in the schemas</param>
        /// <returns>the query object itself</returns>
        public Query LimitKeys(params string[] keys)
        {
            _keys = keys;
            return this;
        }

        /// <summary>
        /// Result's projection - the fields to return by the query
        /// </summary>
        /// <param name="fields">fields a list of TEXT fields in the schemas</param>
        /// <returns>the query object itself</returns>
        public Query ReturnFields(params string[] fields)
        {
            _returnFields = fields;
            _returnFieldsNames = null;
            return this;
        }

        /// <summary>
        /// Result's projection - the fields to return by the query
        /// </summary>
        /// <param name="field">field a list of TEXT fields in the schemas</param>
        /// <returns>the query object itself</returns>
        public Query ReturnFields(params FieldName[] fields)
        {
            _returnFields = null;
            _returnFieldsNames = fields;
            return this;
        }

        public Query HighlightFields(HighlightTags tags, params string[] fields) => HighlightFieldsImpl(tags, fields);
        public Query HighlightFields(params string[] fields) => HighlightFieldsImpl(null, fields);
        private Query HighlightFieldsImpl(HighlightTags? tags, string[] fields)
        {
            if (fields == null || fields.Length > 0)
            {
                _highlightFields = fields;
            }
            _highlightTags = tags;
            _wantsHighlight = true;
            return this;
        }

        public Query SummarizeFields(int contextLen, int fragmentCount, string separator, params string[] fields)
        {
            if (fields == null || fields.Length > 0)
            {
                _summarizeFields = fields;
            }
            _summarizeFragmentLen = contextLen;
            _summarizeNumFragments = fragmentCount;
            _summarizeSeparator = separator;
            _wantsSummarize = true;
            return this;
        }

        public Query SummarizeFields(params string[] fields) => SummarizeFields(-1, -1, null, fields);

        /// <summary>
        /// Set the query to be sorted by a sortable field defined in the schema
        /// </summary>
        /// <param name="field">the sorting field's name</param>
        /// <param name="ascending">if set to true, the sorting order is ascending, else descending</param>
        /// <returns>the query object itself</returns>
        public Query SetSortBy(string field, bool? ascending = null)
        {
            SortBy = field;
            SortAscending = ascending;
            return this;
        }

        /// <summary>
        /// Parameters can be referenced in the query string by a $ , followed by the parameter name,
        /// e.g., $user , and each such reference in the search query to a parameter name is substituted
        /// by the corresponding parameter value.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"> can be String, long or float</param>
        /// <returns>The query object itself</returns>
        public Query AddParam(string name, object value)
        {
            _params.Add(name, value);
            return this;
        }

        public Query Params(Dictionary<string, object> nameValue)
        {
            foreach (var entry in nameValue)
            {
                _params.Add(entry.Key, entry.Value);
            }
            return this;
        }

        /// <summary>
        /// Set the dialect version to execute the query accordingly
        /// </summary>
        /// <param name="dialect"></param>
        /// <returns>the query object itself</returns>
        public Query Dialect(int dialect)
        {
            this.dialect = dialect;
            return this;
        }

        /// <summary>
        /// Set the slop to execute the query accordingly
        /// </summary>
        /// <param name="slop"></param>
        /// <returns>the query object itself</returns>
        public Query Slop(int slop)
        {
            _slop = slop;
            return this;
        }

        /// <summary>
        /// Set the timeout to execute the query accordingly
        /// </summary>
        /// <param name="timeout"></param>
        /// <returns>the query object itself</returns>
        public Query Timeout(long timeout)
        {
            _timeout = timeout;
            return this;
        }

        /// <summary>
        /// Set the query terms appear in the same order in the document as in the query, regardless of the offsets between them
        /// </summary>
        /// <returns>the query object</returns>
        public Query SetInOrder()
        {
            this._inOrder = true;
            return this;
        }

        /// <summary>
        /// Set the query to use a custom query expander instead of the stemmer
        /// </summary>
        /// <param name="field the expander field's name"></param>
        /// <returns>the query object itself</returns>

        public Query SetExpander(String field)
        {
            _expander = field;
            return this;
        }
    }
}