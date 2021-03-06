﻿//-----------------------------------------------------------------------
// <copyright file="MoreLikeThisResponder.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Lucene.Net.Analysis;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Raven.Abstractions.Data;
using Raven.Abstractions.Extensions;
using Raven.Abstractions.Indexing;
using Raven.Database.Extensions;
using Raven.Database.Server.Abstractions;
using Raven.Database.Server.Responders;
using Constants = Raven.Abstractions.Data.Constants;

namespace Raven.Bundles.MoreLikeThis
{
	public class MoreLikeThisResponder : RequestResponder
	{
		public override string UrlPattern
		{
			get { return @"^/morelikethis/([\w\-_]+)/(.+)"; } // /morelikethis/(index-name)/(ravendb-document-id)
		}

		public override string[] SupportedVerbs
		{
			get { return new[] { "GET" }; }
		}

		public override void Respond(IHttpContext context)
		{
			var match = urlMatcher.Match(context.GetRequestUrl());
			var indexName = match.Groups[1].Value;

			var parameters = new MoreLikeThisQueryParameters
								 {
									 DocumentId = match.Groups[2].Value,
									 Fields = context.Request.QueryString.GetValues("fields"),
                                     Boost = context.Request.QueryString.Get("boost").ToNullableBool(),
                                     MaximumNumberOfTokensParsed = context.Request.QueryString.Get("maxNumTokens").ToNullableInt(),
                                     MaximumQueryTerms = context.Request.QueryString.Get("maxQueryTerms").ToNullableInt(),
									 MaximumWordLength = context.Request.QueryString.Get("maxWordLen").ToNullableInt(),
									 MinimumDocumentFrequency = context.Request.QueryString.Get("minDocFreq").ToNullableInt(),
									 MinimumTermFrequency = context.Request.QueryString.Get("minTermFreq").ToNullableInt(),
									 MinimumWordLength = context.Request.QueryString.Get("minWordLen").ToNullableInt(),
                                     StopWordsDocumentId = context.Request.QueryString.Get("stopWords"),
		                         };
            
			var indexDefinition = Database.IndexDefinitionStorage.GetIndexDefinition(indexName);
			if (indexDefinition == null)
			{
				context.SetStatusToNotFound();
				context.WriteJson(new { Error = "The index " + indexName + " cannot be found" });
				return;
			}

			if (string.IsNullOrEmpty(parameters.DocumentId))
			{
				context.SetStatusToBadRequest();
				context.WriteJson(new { Error = "The document id is mandatory" });
				return;
			}

			PerformSearch(context, indexName, indexDefinition, parameters);
		}

		private void PerformSearch(IHttpContext context, string indexName, IndexDefinition indexDefinition, MoreLikeThisQueryParameters parameters)
		{
			IndexSearcher searcher;
			using (Database.IndexStorage.GetCurrentIndexSearcher(indexName, out searcher))
			{
				var td = searcher.Search(new TermQuery(new Term(Constants.DocumentIdFieldName, parameters.DocumentId)), 1);
				// get the current Lucene docid for the given RavenDB doc ID
				if (td.ScoreDocs.Length == 0)
				{
					context.SetStatusToNotFound();
					context.WriteJson(new { Error = "Document " + parameters.DocumentId + " could not be found" });
					return;
				}
				var ir = searcher.GetIndexReader();
				var mlt = new RavenMoreLikeThis(ir);

				AssignParameters(mlt, parameters);

				if (!string.IsNullOrWhiteSpace(parameters.StopWordsDocumentId))
				{
					var stopWordsDoc = Database.Get(parameters.StopWordsDocumentId, null);
					if (stopWordsDoc == null)
					{
						context.SetStatusToNotFound();
						context.WriteJson(
							new
								{
									Error = "Stop words document " + parameters.StopWordsDocumentId + " could not be found"
								});
						return;
					}
					var stopWords = stopWordsDoc.DataAsJson.JsonDeserialization<StopWordsSetup>().StopWords;
					mlt.SetStopWords(new Hashtable(stopWords.ToDictionary(x => x.ToLower())));
				}

				var fieldNames = parameters.Fields ?? GetFieldNames(ir);
				mlt.SetFieldNames(fieldNames);

				mlt.Analyzers = GetAnalyzers(indexDefinition, fieldNames);

				var mltQuery = mlt.Like(td.ScoreDocs[0].doc);
				var tsdc = TopScoreDocCollector.create(context.GetPageSize(Database.Configuration.MaxPageSize), true);
				searcher.Search(mltQuery, tsdc);
				var hits = tsdc.TopDocs().ScoreDocs;
				var documentIds = hits.Select(hit => searcher.Doc(hit.doc).Get(Constants.DocumentIdFieldName)).Distinct();

				var jsonDocuments =
					documentIds
						.Where(docId => string.Equals(docId, parameters.DocumentId, StringComparison.InvariantCultureIgnoreCase) == false)
						.Select(docId => Database.Get(docId, null))
						.Where(it => it != null)
						.ToArray();

				var result = new MultiLoadResult();

				var includedEtags = new List<byte>(jsonDocuments.SelectMany(x => x.Etag.Value.ToByteArray()));
				includedEtags.AddRange(Database.GetIndexEtag(indexName, null).ToByteArray());
				var loadedIds = new HashSet<string>(jsonDocuments.Select(x => x.Key));
				var addIncludesCommand = new AddIncludesCommand(Database, GetRequestTransaction(context), (etag, includedDoc) =>
				{
					includedEtags.AddRange(etag.ToByteArray());
					result.Includes.Add(includedDoc);
				}, context.Request.QueryString.GetValues("include") ?? new string[0], loadedIds);

				foreach (var jsonDocumet in jsonDocuments)
				{
					result.Results.Add(jsonDocumet.ToJson());
					addIncludesCommand.Execute(jsonDocumet.DataAsJson);
				}

				Guid computedEtag;
				using (var md5 = MD5.Create())
				{
					var computeHash = md5.ComputeHash(includedEtags.ToArray());
					computedEtag = new Guid(computeHash);
				}

				if (context.MatchEtag(computedEtag))
				{
					context.SetStatusToNotModified();
					return;
				}

				context.Response.AddHeader("ETag", computedEtag.ToString());
				context.WriteJson(result);
			}
		}

		private static void AssignParameters(Similarity.Net.MoreLikeThis mlt, MoreLikeThisQueryParameters parameters)
		{
			if (parameters.Boost != null) mlt.SetBoost(parameters.Boost.Value);
			if (parameters.MaximumNumberOfTokensParsed != null)
				mlt.SetMaxNumTokensParsed(parameters.MaximumNumberOfTokensParsed.Value);
			if (parameters.MaximumNumberOfTokensParsed != null) mlt.SetMaxNumTokensParsed(parameters.MaximumNumberOfTokensParsed.Value);
			if (parameters.MaximumQueryTerms != null) mlt.SetMaxQueryTerms(parameters.MaximumQueryTerms.Value);
			if (parameters.MaximumWordLength != null) mlt.SetMaxWordLen(parameters.MaximumWordLength.Value);
			if (parameters.MinimumDocumentFrequency != null) mlt.SetMinDocFreq(parameters.MinimumDocumentFrequency.Value);
			if (parameters.MinimumTermFrequency != null) mlt.SetMinTermFreq(parameters.MinimumTermFrequency.Value);
			if (parameters.MinimumWordLength != null) mlt.SetMinWordLen(parameters.MinimumWordLength.Value);
		}

		private static Dictionary<string, Analyzer> GetAnalyzers(IndexDefinition indexDefinition, IEnumerable<string> fieldNames)
		{
			return fieldNames.ToDictionary(fieldName => fieldName, fieldName => indexDefinition.GetAnalyzer(fieldName));
		}

		private static string[] GetFieldNames(IndexReader indexReader)
	    {
            var fields = indexReader.GetFieldNames(IndexReader.FieldOption.INDEXED);
            return fields.Where(x => x != Constants.DocumentIdFieldName).ToArray();
	    }
	}
}
