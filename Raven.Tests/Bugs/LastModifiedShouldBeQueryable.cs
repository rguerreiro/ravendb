using System;
using System.Linq;
using Raven.Abstractions.Linq;
using Raven.Client.Indexes;
using Xunit;

namespace Raven.Tests.Bugs
{
	public class LastModifiedQueries : LocalClientTest
	{
		[Fact]
		public void LastModifiedIsQueryable()
		{
			using(var store = NewDocumentStore())
			{
				new RavenDocumentsByEntityName().Execute(store);

				using (var session = store.OpenSession())
				{
					session.Store(new User {Name = "John Doe"} );
					session.SaveChanges();

					var dateTime = DateTools.DateToString(DateTime.UtcNow, DateTools.Resolution.MILLISECOND);

					var results = session.Advanced.LuceneQuery<object>(new RavenDocumentsByEntityName().IndexName)
						.Where("LastModified:[* TO " + dateTime + "]")
						.WaitForNonStaleResults()
						.ToArray();

					Assert.NotEqual(0, results.Count());
				}
			}
		}

		[Fact]
		public void LastModifiedUsesCorrectDateTimeFormatInIndex()
		{
			using (var store = NewDocumentStore())
			{
				new RavenDocumentsByEntityName().Execute(store);

				var user = new User { Name = "John Doe" };
				using (var session = store.OpenSession())
				{
					session.Store(user);
					session.SaveChanges();
				}

				using (var session = store.OpenSession())
				{
					user = session.Load<User>("users/1");
					var ravenJObject = session.Advanced.GetMetadataFor(user);
					var dateTime = ravenJObject.Value<DateTime>("Last-Modified");
					var results = session.Advanced.LuceneQuery<object>(new RavenDocumentsByEntityName().IndexName)
						.WhereEquals("LastModified", DateTools.DateToString(dateTime, DateTools.Resolution.MILLISECOND))
						.WaitForNonStaleResults()
						.ToArray();
					Assert.Equal(1, results.Count());
				}
			}
		}
	}
}