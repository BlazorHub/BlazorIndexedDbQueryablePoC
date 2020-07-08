using System.Collections.Generic;
using TG.Blazor.IndexedDB;

namespace BlazorIndexedDbQueryablePoC.DB
{
	static class DbModel
	{
		internal static void Configure(DbStore dbStore)
		{
			dbStore.Stores.Add(new StoreSchema
			{
				Name="Employees",
				PrimaryKey=new IndexSpec { Name="id",KeyPath="id",Auto=true },
				Indexes=new List<IndexSpec>
				{
					new IndexSpec{Name="firstName", KeyPath = "firstName", Auto=false},
					new IndexSpec{Name="lastName", KeyPath = "lastName", Auto=false}
				}
			});
		}

		internal static void Configure(DbStoreExtOptions schema)
		{
			schema.Stores.Add("Employees",new StoreSchemaExtOptions().As<Person>());
		}
	}
}
