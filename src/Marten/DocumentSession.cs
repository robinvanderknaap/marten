﻿using System;
using System.Collections.Generic;
using System.Linq;
using FubuCore;
using Marten.Linq;
using Marten.Schema;
using Npgsql;
using Remotion.Linq.Parsing.ExpressionVisitors.Transformation;
using Remotion.Linq.Parsing.Structure;
using Remotion.Linq.Parsing.Structure.NodeTypeProviders;

namespace Marten
{
    public class DocumentSession : IDocumentSession
    {
        private readonly IDocumentSchema _schema;
        private readonly ISerializer _serializer;
        private readonly IConnectionFactory _factory;

        private readonly IList<object> _updates = new List<object>();
        private readonly IList<NpgsqlCommand> _deletes = new List<NpgsqlCommand>(); 

        public DocumentSession(IDocumentSchema schema, ISerializer serializer, IConnectionFactory factory)
        {
            _schema = schema;
            _serializer = serializer;
            _factory = factory;
        }

        public void Dispose()
        {
        }

        public void Delete<T>(T entity)
        {
            var storage = _schema.StorageFor(typeof (T));
            _deletes.Add(storage.DeleteCommandForEntity(entity));
        }

        public void Delete<T>(ValueType id)
        {
            var storage = _schema.StorageFor(typeof(T));
            _deletes.Add(storage.DeleteCommandForId(id));
        }

        public void Delete<T>(string id)
        {
            var storage = _schema.StorageFor(typeof(T));
            _deletes.Add(storage.DeleteCommandForId(id));
        }

        public T Load<T>(string id)
        {
            throw new NotImplementedException();
        }

        public T[] Load<T>(IEnumerable<string> ids)
        {
            throw new NotImplementedException();
        }

        public T Load<T>(ValueType id)
        {
            var storage = _schema.StorageFor(typeof (T));
            var loader = storage.LoaderCommand(id);

            using (var conn = _factory.Create())
            {
                conn.Open();

                loader.Connection = conn;
                var json = loader.ExecuteScalar() as string; // Maybe do this as a stream later for big docs?

                if (json == null) return default(T);

                return _serializer.FromJson<T>(json);
            }
        }

        public T[] Load<T>(params ValueType[] ids)
        {
            throw new NotImplementedException();
        }

        public T[] Load<T>(IEnumerable<ValueType> ids)
        {
            throw new NotImplementedException();
        }

        public void SaveChanges()
        {
            // TODO -- fancier later to add batch updating!

            using (var conn = _factory.Create())
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    _updates.Each(o =>
                    {
                        var docType = o.GetType();
                        var storage = _schema.StorageFor(docType);

                        using (var command = storage.UpsertCommand(o, _serializer.ToJson(o)))
                        {
                            command.Connection = conn;
                            command.Transaction = tx;

                            command.ExecuteNonQuery();
                        }
                    });

                    _deletes.Each(cmd =>
                    {
                        cmd.Connection = conn;
                        cmd.Transaction = tx;
                        cmd.ExecuteNonQuery();
                    });

                    tx.Commit();

                    _deletes.Clear();
                    _updates.Clear();
                }
            }
            

        }

        public void Store(object entity)
        {
            // TODO -- throw if null
            _updates.Add(entity);
        }

        public IQueryable<T> Query<T>()
        {
            var transformerRegistry = ExpressionTransformerRegistry.CreateDefault();


            var processor = ExpressionTreeParser.CreateDefaultProcessor(transformerRegistry);
            // Add custom processors here:
            // processor.InnerProcessors.Add (new MyExpressionTreeProcessor());


            var expressionTreeParser = new ExpressionTreeParser(new MethodNameBasedNodeTypeRegistry(), processor);
            var parser = new QueryParser(expressionTreeParser);
            return new MartenQueryable<T>(new QueryProvider(parser, new QueryExecutor()));
        }

        public IEnumerable<T> Query<T>(string @where, string orderBy = null)
        {
            var tableName = _schema.StorageFor(typeof(T)).TableName;
            var sql = "select data from {0} where {1}".ToFormat(tableName, @where);
            if (orderBy.IsNotEmpty())
            {
                sql += " order by " + orderBy;
            }

            return query<T>(sql).ToArray();
        }

        private IEnumerable<T> query<T>(string sql)
        {
            using (var conn = _factory.Create())
            {
                conn.Open();

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var json = reader.GetString(0);
                            yield return _serializer.FromJson<T>(json);
                        }
                    }
                }
            }
        } 
    }
}