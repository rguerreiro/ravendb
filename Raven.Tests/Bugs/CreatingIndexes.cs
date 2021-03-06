using System.ComponentModel.Composition.Hosting;
using System.Linq;
using Raven.Client.Indexes;
using Xunit;

namespace Raven.Tests.Bugs
{
	public class CreatingIndexes : LocalClientTest
	{
		public class AllDocs1 : AbstractIndexCreationTask<object>
		{
			public AllDocs1()
			{
				Map = docs => from doc in docs select new { x = 1 };
			}
		}

		public class AllDocs2 : AbstractIndexCreationTask<object>
		{
			public AllDocs2()
			{
				Map = docs => from doc in docs select new { };
			}
		}

		[Fact]
		public void CanCreateIndexes()
		{
			using(var store = NewDocumentStore())
			{
				var container = new CompositionContainer(new TypeCatalog(typeof (AllDocs1), typeof (AllDocs2)));
				IndexCreation.CreateIndexes(container, store);
			}
		}
	}
}