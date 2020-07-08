using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TG.Blazor.IndexedDB;

namespace BlazorIndexedDbQueryablePoC
{
	public class DbStoreExtOptions
	{
		public IDictionary<string,StoreSchemaExtOptions> Stores { get; } = new Dictionary<string,StoreSchemaExtOptions>(Utils.StringOrdinalComparer);
	}

	static class Utils
	{
		internal static IEqualityComparer<string> StringOrdinalComparer { get; } = new StringOrdinalEqualityComparer();

		class StringOrdinalEqualityComparer:IEqualityComparer<string>
		{
			public bool Equals(string x,string y)
				=> EqualsOrdinal(x,y);

			public int GetHashCode(string obj)
				=> obj.GetHashCode();
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal static bool EqualsOrdinal(string x,string y)
			=> string.Equals(x,y,StringComparison.Ordinal);
	}

	public class StoreSchemaExtOptions
	{
		public Type ModelType { get; set; }

		//internal string[] IndexPriorities { get; set; }

		public StoreSchemaExtOptions As<TModelType>()
		{
			ModelType=typeof(TModelType);
			return this;
		}
	}

	public interface IIndexedDB
	{
		IStore<T> Store<T>() where T : new();
		IStore<T> Store<T>(string name) where T : new();
	}

	public interface IQueryable<T>:IAsyncEnumerable<T> where T : new()
	{
		IQueryParameters<T> Parameters { get; }
		IQueryProvider<T> Provider { get; }
	}

	public interface IQueryParameters<T>
	{
		ICollection<Expression<Func<T,bool>>> WhereClausules { get; }
	}

	public interface IQueryProvider<T> where T : new()
	{
		IQueryable<T> CreateQuery(IQueryable<T> source,IQueryParameters<T> parameters);

		IAsyncEnumerable<T> ExecuteAsync(IQueryable<T> query);
	}

	public interface IStore<T>:IQueryable<T> where T : new()
	{
		Task AddAsync(T model);
		Task Update(T model);
		Task DeleteAsync<TId>(TId id);
	}

	class DefaultIndexedDB:IIndexedDB
	{
		readonly IndexedDBManager _db;
		readonly DbStoreExtOptions _options;
		readonly IDictionary<Type,string> _storeNamesIndex = new Dictionary<Type,string>();
		public DefaultIndexedDB(IndexedDBManager db,IOptions<DbStoreExtOptions> options)
		{
			_db=db;
			_options=options.Value;
			RefreshStoreNamesIndex();
		}

		void RefreshStoreNamesIndex()
		{
			IEnumerable<(Type ModelType, string StoreName)> newIndex = _options.Stores.Select(x => (StoreName: x.Key, x.Value.ModelType)).Where(x => x.ModelType!=null).GroupBy(x => x.ModelType).Where(x => !x.Skip(1).Any()).Select(x => (ModelType: x.Key, x.First().StoreName));
			_storeNamesIndex.Clear();
			foreach ((Type modelType, string storeName) in newIndex)
				_storeNamesIndex.Add(modelType,storeName);
		}

		public IStore<T> Store<T>() where T : new()
		{
			Type type = typeof(T);
			if (_storeNamesIndex.TryGetValue(type,out string name))
				return Store<T>(name);
			throw new IndexOutOfRangeException($"Type {type} has no default store. Declare it explicitely.");
		}

		public IStore<T> Store<T>(string name) where T : new()
		{
			StoreSchema schema = _db.Stores.FirstOrDefault(x => Utils.EqualsOrdinal(x.Name,name));
			if (schema==null)
				throw new InvalidOperationException($"No {name} schema defined.");
			if (!_options.Stores.TryGetValue(name,out StoreSchemaExtOptions options))
				throw new InvalidOperationException($"No {name} schema options defined.");
			return (Store<T>)Activator.CreateInstance(typeof(Store<>).MakeGenericType(options.ModelType),_db,schema,options); //new Store(schema,options);
		}
	}

	class Store<T>:IStore<T>, IQueryProvider<T> where T : new()
	{
		readonly IndexedDBManager _db;
		readonly StoreSchema _schema;
		readonly StoreSchemaExtOptions _options;
		public Store(IndexedDBManager db,StoreSchema schema,StoreSchemaExtOptions options)
		{
			_db=db;
			_schema=schema;
			_options=options;

			_parameters=new QueryParameters<T>();
			_provider=this;
		}

		//Interception?

		IAsyncEnumerator<T> IAsyncEnumerable<T>.GetAsyncEnumerator(CancellationToken cancellationToken)
			=> _provider.ExecuteAsync(this).GetAsyncEnumerator(cancellationToken);

		public Task AddAsync(T model)
			=> _db.AddRecord(CreateStoreRecord(model));

		public Task DeleteAsync<TId>(TId id)
			=> _db.DeleteRecord(_schema.Name,id);

		public Task Update(T model)
			=> _db.UpdateRecord(CreateStoreRecord(model));

		StoreRecord<T> CreateStoreRecord(T model)
			=> new StoreRecord<T>() { Storename=_schema.Name,Data=model };

		////////////////

		IQueryParameters<T> _parameters;
		IQueryParameters<T> IQueryable<T>.Parameters => _parameters;
		IQueryProvider<T> _provider;
		IQueryProvider<T> IQueryable<T>.Provider => _provider;

		IQueryable<T> IQueryProvider<T>.CreateQuery(IQueryable<T> source,IQueryParameters<T> parameters)
			=> new Queryable<T>(source.Provider,parameters);

		IAsyncEnumerable<T> IQueryProvider<T>.ExecuteAsync(IQueryable<T> query)
		{
			object res = null;
			ICollection<Expression<Func<T,bool>>> whereClausules = query.Parameters.WhereClausules;
			if (whereClausules.Count!=0)
			{
				(string PropertyName, object Value)[] candidates = whereClausules.Select(ExpressionParser.ParseToIndexInfo).Where(x => x.IsValid).Select(x => (x.PropertyName, x.Value)).ToArray();
				if (candidates.Length!=0)
				{
					foreach (IndexSpec index in Enumerable.Repeat(_schema.PrimaryKey,1).Concat(_schema.Indexes))
					{
						(string _, object IndexValue) indexImpl = candidates.FirstOrDefault(candidate => Utils.EqualsOrdinal(CamelCase(candidate.PropertyName),index.KeyPath));
						if (indexImpl!=default)
						{
							//It's difficult to create StoreIndexQuery instance and call GetAllRecordsByIndex if query value type is unknown at compile time; because all is generic.
							//new StoreIndexQuery<T> { Storename=_schema.Name,AllMatching=true,IndexName=index.Name,QueryValue=indexValue }
							object indexValue = indexImpl.IndexValue;
							Type indexValueType = indexValue.GetType();
							Type storeIndexQueryType = typeof(StoreIndexQuery<>).MakeGenericType(indexValueType);
							object searchQuery = Activator.CreateInstance(storeIndexQueryType);
							storeIndexQueryType.GetProperty(nameof(StoreIndexQuery<int>.Storename)).SetValue(searchQuery,_schema.Name);
							storeIndexQueryType.GetProperty(nameof(StoreIndexQuery<int>.AllMatching)).SetValue(searchQuery,true);
							storeIndexQueryType.GetProperty(nameof(StoreIndexQuery<int>.IndexName)).SetValue(searchQuery,index.Name);
							storeIndexQueryType.GetProperty(nameof(StoreIndexQuery<int>.QueryValue)).SetValue(searchQuery,indexValue);

							//res=_db.GetAllRecordsByIndex(searchQuery);
							res=_db.GetType().GetMethod(nameof(IndexedDBManager.GetAllRecordsByIndex)).MakeGenericMethod(indexValueType,typeof(T))
								.Invoke(_db,new object[] { searchQuery });

							//We found the best index, no need to try another. Let's get out of the loop.
							break;
						}
					}
				}
			}
			if (res==null)
				res=_db.GetRecords<T>(_schema.Name); //GetAllRecordsByIndex vs. GetRecords don't return consist type (IList<> vs. List<>)
																 //obsah tasku muze byt null. Musim vyhodit vyjimku. Pouzita library vyjimky nepropaguje :-(.
			return new AsyncEnumerable((Task)res);
		}

		////////////////

		string CamelCase(string name)
			=> JsonNamingPolicy.CamelCase.ConvertName(name);

		class AsyncEnumerable:IAsyncEnumerable<T>
		{
			readonly Task _task;

			public AsyncEnumerable(Task task)
			{
				_task=task;
			}

			public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
				=> new AsyncEnumerator(_task);
		}

		class AsyncEnumerator:IAsyncEnumerator<T>
		{
			readonly Task _task;
			bool _isStart = true;
			IEnumerator<T> _enumerator;

			public AsyncEnumerator(Task task)
			{
				_task=task;
			}

			public T Current
			{
				get
				{
					if (_enumerator==null)
						throw new InvalidOperationException($"Enumeration not started yet. Call {nameof(MoveNextAsync)} first.");
					return _enumerator.Current;
				}
			}

			public ValueTask DisposeAsync()
			{
				_enumerator?.Dispose();
				return new ValueTask();
			}

			public async ValueTask<bool> MoveNextAsync()
			{
				if (_isStart)
				{
					//IEnumerable<T> taskResult = await _task;
					await Task.WhenAll(_task);
					IEnumerable<T> taskResult = (IEnumerable<T>)_task.GetType().GetProperty(nameof(Task<int>.Result)).GetValue(_task);
					if (taskResult==null)
						throw new Exception("Error getting data during TG.Blazor.IndexedDB.IndexedDBManager.CallJavascript call.");
					_enumerator=taskResult.GetEnumerator();
					_isStart=false;
				}

				return _enumerator.MoveNext();
			}
		}

		#region ExpressionParser
		static class ExpressionParser
		{
			internal static (bool IsValid, string PropertyName, object Value) ParseToIndexInfo<T>(Expression<Func<T,bool>> expression)
			{
				(bool mainInfo_IsUsable, string mainInfo_ParameterName, BinaryExpression mainInfo_Body)=GetMainInfo(expression);
				if (mainInfo_IsUsable)
				{
					bool valueInfo_IsValid; object valueInfo_Value;
					(bool modelAccessInfo_IsValid, PropertyInfo modelAccessInfo_Property)=GetModelAccessInfo<T>(mainInfo_Body.Left,mainInfo_ParameterName);
					if (modelAccessInfo_IsValid)
						(valueInfo_IsValid, valueInfo_Value)=GetValueInfo(mainInfo_Body.Right);
					else
					{
						(modelAccessInfo_IsValid, modelAccessInfo_Property)=GetModelAccessInfo<T>(mainInfo_Body.Right,mainInfo_ParameterName);
						if (!modelAccessInfo_IsValid)
							return (false, null, null);

						(valueInfo_IsValid, valueInfo_Value)=GetValueInfo(mainInfo_Body.Left);
					}

					if (valueInfo_IsValid)
						return (true, modelAccessInfo_Property.Name, valueInfo_Value);
				}
				return (false, null, null);
			}

			static (bool IsUsable, string ParameterName, BinaryExpression Body) GetMainInfo<T>(Expression<Func<T,bool>> expression)
			{
				if ((expression.NodeType!=ExpressionType.Lambda)||(expression.ReturnType!=typeof(bool))||(expression.Parameters.Count!=1))
					return (false, null, null);
				ParameterExpression parameter = expression.Parameters[0];
				if (parameter.Type!=typeof(T))
					return (false, null, null);

				Expression body = expression.Body;
				if ((body.NodeType!=ExpressionType.Equal)||(!(body is BinaryExpression binaryExpression)))
					return (false, null, null);

				return (true, parameter.Name, binaryExpression);
			}

			static (bool IsValid, PropertyInfo Property) GetModelAccessInfo<T>(Expression expression,string parameterName)
			{
				if ((expression.NodeType!=ExpressionType.MemberAccess)||(!(expression is MemberExpression memberExpression)))
					return (false, null);

				Expression container = memberExpression.Expression;
				if ((container.NodeType!=ExpressionType.Parameter)||(container.Type!=typeof(T))||(!(container is ParameterExpression parameter))||(!Utils.EqualsOrdinal(parameter.Name,parameterName)))
					return (false, null);

				MemberInfo memberInfo = memberExpression.Member;
				if ((memberInfo.MemberType!=MemberTypes.Property)||(!(memberInfo is PropertyInfo propertyInfo))||(!propertyInfo.CanRead))
					return (false, null);

				return (true, propertyInfo);
			}

			static (bool IsValid, object value) GetValueInfo(Expression expression)
			{
				if ((expression.NodeType==ExpressionType.Constant)&&(expression is ConstantExpression constant))
					return (true, constant.Value);

				if ((expression.NodeType==ExpressionType.MemberAccess)&&(expression is MemberExpression member))
					return GetValueInfoMember(member);

				return (false, null);
			}

			static (bool IsValid, object value) GetValueInfoMember(MemberExpression expression)
			{
				Expression container = expression.Expression;
				if ((container.NodeType!=ExpressionType.Constant)||(!(container is ConstantExpression constant)))
					return (false, null);

				object containerObj = constant.Value;

				MemberInfo memberInfo = expression.Member;
				if ((memberInfo.MemberType==MemberTypes.Property)&&(memberInfo is PropertyInfo property))
					return property.CanRead
						? (true, property.GetValue(containerObj))
						: (false, null);

				if ((memberInfo.MemberType==MemberTypes.Field)&&(memberInfo is FieldInfo field))
					return (true, field.GetValue(containerObj));

				return (false, null);
			}
		}
		#endregion ExpressionParser
	}

	class QueryParameters<T>:IQueryParameters<T>
	{
		ICollection<Expression<Func<T,bool>>> _whereClausules;
		public ICollection<Expression<Func<T,bool>>> WhereClausules { get { return _whereClausules??(_whereClausules=new List<Expression<Func<T,bool>>>()); } private set { _whereClausules=value; } }

		internal static QueryParameters<T> CloneFrom(IQueryParameters<T> source)
			=> new QueryParameters<T>() { WhereClausules=new List<Expression<Func<T,bool>>>(source.WhereClausules) };
	}

	class Queryable<T>:IQueryable<T> where T : new()
	{
		readonly IQueryProvider<T> _provider;
		readonly IQueryParameters<T> _parameters;

		internal Queryable(IQueryProvider<T> provider,IQueryParameters<T> parameters)
		{
			_provider=provider;
			_parameters=parameters;
		}

		public IQueryParameters<T> Parameters => _parameters;

		public IQueryProvider<T> Provider => _provider;

		public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
			=> _provider.ExecuteAsync(this).GetAsyncEnumerator(cancellationToken);
	}

	public static class QueryableExtensions
	{
		public static IQueryable<TSource> Where<TSource>(this IQueryable<TSource> source,Expression<Func<TSource,bool>> expression) where TSource : new()
		{
			QueryParameters<TSource> parms = QueryParameters<TSource>.CloneFrom(source.Parameters);
			parms.WhereClausules.Add(expression);
			return source.Provider.CreateQuery(source,parms);
		}

		//Take
		//Skip
		//First
		//FirstOrDefault

		public async static Task<TSource[]> ToArrayAsync<TSource>(this IQueryable<TSource> source,CancellationToken cancellationToken = default) where TSource : new()
		{
			List<TSource> items = new List<TSource>();
			await using (IAsyncEnumerator<TSource> enumerator = source.GetAsyncEnumerator(cancellationToken))
				while (await enumerator.MoveNextAsync())
					items.Add(enumerator.Current);
			return items.ToArray();
		}
	}
}

namespace Microsoft.Extensions.DependencyInjection
{
	public static class IndexedDbQueryingExtensions
	{
		public static IServiceCollection AddQuerying(this IServiceCollection services,Action<BlazorIndexedDbQueryablePoC.DbStoreExtOptions> configure)
		{
			services.Configure(configure);
			services.AddScoped<BlazorIndexedDbQueryablePoC.IIndexedDB,BlazorIndexedDbQueryablePoC.DefaultIndexedDB>();
			return services;
		}
	}
}