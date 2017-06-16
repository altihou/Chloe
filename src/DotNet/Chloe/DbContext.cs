﻿using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Linq;
using Chloe.Query;
using Chloe.Core;
using Chloe.Infrastructure;
using Chloe.Descriptors;
using Chloe.DbExpressions;
using Chloe.Query.Internals;
using Chloe.Core.Visitors;
using Chloe.Exceptions;
using System.Data;
using Chloe.InternalExtensions;
using Chloe.Extensions;

namespace Chloe
{
    public abstract class DbContext : IDbContext, IDisposable
    {
        bool _disposed = false;
        InternalAdoSession _adoSession;
        DbSession _session;

        Dictionary<Type, TrackEntityCollection> _trackingEntityContainer;

        Dictionary<Type, TrackEntityCollection> TrackingEntityContainer
        {
            get
            {
                if (this._trackingEntityContainer == null)
                {
                    this._trackingEntityContainer = new Dictionary<Type, TrackEntityCollection>();
                }

                return this._trackingEntityContainer;
            }
        }

        internal InternalAdoSession AdoSession
        {
            get
            {
                this.CheckDisposed();
                if (this._adoSession == null)
                    this._adoSession = new InternalAdoSession(this.DbContextServiceProvider.CreateConnection());
                return this._adoSession;
            }
        }
        public abstract IDbContextServiceProvider DbContextServiceProvider { get; }

        protected DbContext()
        {
            this._session = new DbSession(this);
        }

        public IDbSession Session { get { return this._session; } }


        public virtual IQuery<TEntity> Query<TEntity>()
        {
            return this.Query<TEntity>(null);
        }
        public virtual IQuery<TEntity> Query<TEntity>(string table)
        {
            return new Query<TEntity>(this, table);
        }
        public virtual TEntity QueryByKey<TEntity>(object key, bool tracking = false)
        {
            return this.QueryByKey<TEntity>(key, null, tracking);
        }
        public virtual TEntity QueryByKey<TEntity>(object key, string table, bool tracking = false)
        {
            Expression<Func<TEntity, bool>> predicate = BuildPredicate<TEntity>(key);
            var q = this.Query<TEntity>(table).Where(predicate);

            if (tracking)
                q = q.AsTracking();

            return q.FirstOrDefault();
        }

        public virtual IJoiningQuery<T1, T2> JoinQuery<T1, T2>(Expression<Func<T1, T2, object[]>> joinInfo)
        {
            List<Tuple<JoinType, Expression>> joinInfos = ResolveJoinInfo(joinInfo);
            var ret = this.Query<T1>()
                .Join<T2>(joinInfos[0].Item1, (Expression<Func<T1, T2, bool>>)joinInfos[0].Item2);

            return ret;
        }
        public virtual IJoiningQuery<T1, T2, T3> JoinQuery<T1, T2, T3>(Expression<Func<T1, T2, T3, object[]>> joinInfo)
        {
            List<Tuple<JoinType, Expression>> joinInfos = ResolveJoinInfo(joinInfo);
            var ret = this.Query<T1>()
                .Join<T2>(joinInfos[0].Item1, (Expression<Func<T1, T2, bool>>)joinInfos[0].Item2)
                .Join<T3>(joinInfos[1].Item1, (Expression<Func<T1, T2, T3, bool>>)joinInfos[1].Item2);

            return ret;
        }
        public virtual IJoiningQuery<T1, T2, T3, T4> JoinQuery<T1, T2, T3, T4>(Expression<Func<T1, T2, T3, T4, object[]>> joinInfo)
        {
            List<Tuple<JoinType, Expression>> joinInfos = ResolveJoinInfo(joinInfo);
            var ret = this.Query<T1>()
                .Join<T2>(joinInfos[0].Item1, (Expression<Func<T1, T2, bool>>)joinInfos[0].Item2)
                .Join<T3>(joinInfos[1].Item1, (Expression<Func<T1, T2, T3, bool>>)joinInfos[1].Item2)
                .Join<T4>(joinInfos[2].Item1, (Expression<Func<T1, T2, T3, T4, bool>>)joinInfos[2].Item2);

            return ret;
        }
        public virtual IJoiningQuery<T1, T2, T3, T4, T5> JoinQuery<T1, T2, T3, T4, T5>(Expression<Func<T1, T2, T3, T4, T5, object[]>> joinInfo)
        {
            List<Tuple<JoinType, Expression>> joinInfos = ResolveJoinInfo(joinInfo);
            var ret = this.Query<T1>()
                .Join<T2>(joinInfos[0].Item1, (Expression<Func<T1, T2, bool>>)joinInfos[0].Item2)
                .Join<T3>(joinInfos[1].Item1, (Expression<Func<T1, T2, T3, bool>>)joinInfos[1].Item2)
                .Join<T4>(joinInfos[2].Item1, (Expression<Func<T1, T2, T3, T4, bool>>)joinInfos[2].Item2)
                .Join<T5>(joinInfos[3].Item1, (Expression<Func<T1, T2, T3, T4, T5, bool>>)joinInfos[3].Item2);

            return ret;
        }

        public virtual IEnumerable<T> SqlQuery<T>(string sql, params DbParam[] parameters)
        {
            return this.SqlQuery<T>(sql, CommandType.Text, parameters);
        }
        public virtual IEnumerable<T> SqlQuery<T>(string sql, CommandType cmdType, params DbParam[] parameters)
        {
            Utils.CheckNull(sql, "sql");
            return new InternalSqlQuery<T>(this, sql, cmdType, parameters);
        }

        public virtual TEntity Insert<TEntity>(TEntity entity)
        {
            return this.Insert(entity, null);
        }
        public virtual TEntity Insert<TEntity>(TEntity entity, string table)
        {
            Utils.CheckNull(entity);

            TypeDescriptor typeDescriptor = TypeDescriptor.GetDescriptor(entity.GetType());

            MappingMemberDescriptor keyMemberDescriptor = typeDescriptor.PrimaryKey;

            object keyValue = null;

            MappingMemberDescriptor autoIncrementMemberDescriptor = typeDescriptor.AutoIncrement;

            Dictionary<MappingMemberDescriptor, DbExpression> insertColumns = new Dictionary<MappingMemberDescriptor, DbExpression>();
            foreach (var kv in typeDescriptor.MappingMemberDescriptors)
            {
                MappingMemberDescriptor memberDescriptor = kv.Value;

                if (memberDescriptor == autoIncrementMemberDescriptor)
                    continue;

                object val = memberDescriptor.GetValue(entity);

                if (memberDescriptor == keyMemberDescriptor)
                {
                    keyValue = val;
                }

                DbExpression valExp = DbExpression.Parameter(val, memberDescriptor.MemberInfoType);
                insertColumns.Add(memberDescriptor, valExp);
            }

            if (keyMemberDescriptor != null)
            {
                //主键为空并且主键又不是自增列
                if (keyValue == null && keyMemberDescriptor != autoIncrementMemberDescriptor)
                {
                    throw new ChloeException(string.Format("The primary key '{0}' could not be null.", keyMemberDescriptor.MemberInfo.Name));
                }
            }

            DbTable dbTable = table == null ? typeDescriptor.Table : new DbTable(table, typeDescriptor.Table.Schema);
            DbInsertExpression e = new DbInsertExpression(dbTable);

            foreach (var kv in insertColumns)
            {
                e.InsertColumns.Add(kv.Key.Column, kv.Value);
            }

            if (autoIncrementMemberDescriptor == null)
            {
                this.ExecuteSqlCommand(e);
                return entity;
            }

            IDbExpressionTranslator translator = this.DbContextServiceProvider.CreateDbExpressionTranslator();
            List<DbParam> parameters;
            string sql = translator.Translate(e, out parameters);

            sql = string.Concat(sql, ";", this.GetSelectLastInsertIdClause());

            //SELECT @@IDENTITY 返回的是 decimal 类型
            object retIdentity = this.Session.ExecuteScalar(sql, parameters.ToArray());

            if (retIdentity == null || retIdentity == DBNull.Value)
            {
                throw new ChloeException("Unable to get the identity value.");
            }

            retIdentity = ConvertIdentityType(retIdentity, autoIncrementMemberDescriptor.MemberInfoType);
            autoIncrementMemberDescriptor.SetValue(entity, retIdentity);
            return entity;
        }
        public virtual object Insert<TEntity>(Expression<Func<TEntity>> body)
        {
            return this.Insert(body, null);
        }
        public virtual object Insert<TEntity>(Expression<Func<TEntity>> body, string table)
        {
            Utils.CheckNull(body);

            TypeDescriptor typeDescriptor = TypeDescriptor.GetDescriptor(typeof(TEntity));

            MappingMemberDescriptor keyMemberDescriptor = typeDescriptor.PrimaryKey;
            MappingMemberDescriptor autoIncrementMemberDescriptor = typeDescriptor.AutoIncrement;

            Dictionary<MemberInfo, Expression> insertColumns = InitMemberExtractor.Extract(body);

            DbTable explicitDbTable = null;
            if (table != null)
                explicitDbTable = new DbTable(table, typeDescriptor.Table.Schema);
            DefaultExpressionParser expressionParser = typeDescriptor.GetExpressionParser(explicitDbTable);
            DbInsertExpression e = new DbInsertExpression(explicitDbTable ?? typeDescriptor.Table);

            object keyVal = null;

            foreach (var kv in insertColumns)
            {
                MemberInfo key = kv.Key;
                MappingMemberDescriptor memberDescriptor = typeDescriptor.TryGetMappingMemberDescriptor(key);

                if (memberDescriptor == null)
                    throw new ChloeException(string.Format("The member '{0}' does not map any column.", key.Name));

                if (memberDescriptor == autoIncrementMemberDescriptor)
                    throw new ChloeException(string.Format("Could not insert value into the identity column '{0}'.", memberDescriptor.Column.Name));

                if (memberDescriptor.IsPrimaryKey)
                {
                    object val = ExpressionEvaluator.Evaluate(kv.Value);
                    if (val == null)
                        throw new ChloeException(string.Format("The primary key '{0}' could not be null.", memberDescriptor.MemberInfo.Name));
                    else
                    {
                        keyVal = val;
                        e.InsertColumns.Add(memberDescriptor.Column, DbExpression.Parameter(keyVal));
                        continue;
                    }
                }

                e.InsertColumns.Add(memberDescriptor.Column, expressionParser.Parse(kv.Value));
            }

            if (keyMemberDescriptor != null)
            {
                //主键为空并且主键又不是自增列
                if (keyVal == null && keyMemberDescriptor != autoIncrementMemberDescriptor)
                {
                    throw new ChloeException(string.Format("The primary key '{0}' could not be null.", keyMemberDescriptor.MemberInfo.Name));
                }
            }

            if (keyMemberDescriptor == null || keyMemberDescriptor != autoIncrementMemberDescriptor)
            {
                this.ExecuteSqlCommand(e);
                return keyVal; /* It will return null if an entity does not define primary key. */
            }

            IDbExpressionTranslator translator = this.DbContextServiceProvider.CreateDbExpressionTranslator();
            List<DbParam> parameters;
            string sql = translator.Translate(e, out parameters);
            sql = string.Concat(sql, ";", this.GetSelectLastInsertIdClause());

            //SELECT @@IDENTITY 返回的是 decimal 类型
            object retIdentity = this.Session.ExecuteScalar(sql, parameters.ToArray());

            if (retIdentity == null || retIdentity == DBNull.Value)
            {
                throw new ChloeException("Unable to get the identity value.");
            }

            retIdentity = ConvertIdentityType(retIdentity, autoIncrementMemberDescriptor.MemberInfoType);
            return retIdentity;
        }

        public virtual int Update<TEntity>(TEntity entity)
        {
            return this.Update(entity, null);
        }
        public virtual int Update<TEntity>(TEntity entity, string table)
        {
            Utils.CheckNull(entity);

            TypeDescriptor typeDescriptor = TypeDescriptor.GetDescriptor(entity.GetType());
            EnsureEntityHasPrimaryKey(typeDescriptor);

            MappingMemberDescriptor keyMemberDescriptor = typeDescriptor.PrimaryKey;
            MemberInfo keyMember = keyMemberDescriptor.MemberInfo;

            object keyVal = null;

            IEntityState entityState = this.TryGetTrackedEntityState(entity);
            Dictionary<MappingMemberDescriptor, DbExpression> updateColumns = new Dictionary<MappingMemberDescriptor, DbExpression>();
            foreach (var kv in typeDescriptor.MappingMemberDescriptors)
            {
                MemberInfo member = kv.Key;
                MappingMemberDescriptor memberDescriptor = kv.Value;

                if (member == keyMember)
                {
                    keyVal = memberDescriptor.GetValue(entity);
                    keyMemberDescriptor = memberDescriptor;
                    continue;
                }

                if (memberDescriptor.IsAutoIncrement)
                    continue;

                object val = memberDescriptor.GetValue(entity);

                if (entityState != null && !entityState.HasChanged(memberDescriptor, val))
                    continue;

                DbExpression valExp = DbExpression.Parameter(val, memberDescriptor.MemberInfoType);
                updateColumns.Add(memberDescriptor, valExp);
            }

            if (keyVal == null)
                throw new ChloeException(string.Format("The primary key '{0}' could not be null.", keyMember.Name));

            if (updateColumns.Count == 0)
                return 0;

            DbTable dbTable = table == null ? typeDescriptor.Table : new DbTable(table, typeDescriptor.Table.Schema);
            DbExpression left = new DbColumnAccessExpression(dbTable, keyMemberDescriptor.Column);
            DbExpression right = DbExpression.Parameter(keyVal, keyMemberDescriptor.MemberInfoType);
            DbExpression conditionExp = new DbEqualExpression(left, right);

            DbUpdateExpression e = new DbUpdateExpression(dbTable, conditionExp);

            foreach (var item in updateColumns)
            {
                e.UpdateColumns.Add(item.Key.Column, item.Value);
            }

            int ret = this.ExecuteSqlCommand(e);
            if (entityState != null)
                entityState.Refresh();
            return ret;
        }
        public virtual int Update<TEntity>(Expression<Func<TEntity, bool>> condition, Expression<Func<TEntity, TEntity>> body)
        {
            return this.Update(condition, body, null);
        }
        public virtual int Update<TEntity>(Expression<Func<TEntity, bool>> condition, Expression<Func<TEntity, TEntity>> body, string table)
        {
            Utils.CheckNull(condition);
            Utils.CheckNull(body);

            TypeDescriptor typeDescriptor = TypeDescriptor.GetDescriptor(typeof(TEntity));

            Dictionary<MemberInfo, Expression> updateColumns = InitMemberExtractor.Extract(body);

            DbTable explicitDbTable = null;
            if (table != null)
                explicitDbTable = new DbTable(table, typeDescriptor.Table.Schema);
            DefaultExpressionParser expressionParser = typeDescriptor.GetExpressionParser(explicitDbTable);

            DbExpression conditionExp = expressionParser.ParseFilterPredicate(condition);

            DbUpdateExpression e = new DbUpdateExpression(explicitDbTable ?? typeDescriptor.Table, conditionExp);

            foreach (var kv in updateColumns)
            {
                MemberInfo key = kv.Key;
                MappingMemberDescriptor memberDescriptor = typeDescriptor.TryGetMappingMemberDescriptor(key);

                if (memberDescriptor == null)
                    throw new ChloeException(string.Format("The member '{0}' does not map any column.", key.Name));

                if (memberDescriptor.IsPrimaryKey)
                    throw new ChloeException(string.Format("Could not update the primary key '{0}'.", memberDescriptor.Column.Name));

                if (memberDescriptor.IsAutoIncrement)
                    throw new ChloeException(string.Format("Could not update the identity column '{0}'.", memberDescriptor.Column.Name));

                e.UpdateColumns.Add(memberDescriptor.Column, expressionParser.Parse(kv.Value));
            }

            if (e.UpdateColumns.Count == 0)
                return 0;

            return this.ExecuteSqlCommand(e);

        }

        public virtual int Delete<TEntity>(TEntity entity)
        {
            return this.Delete(entity, null);
        }
        public virtual int Delete<TEntity>(TEntity entity, string table)
        {
            Utils.CheckNull(entity);

            TypeDescriptor typeDescriptor = TypeDescriptor.GetDescriptor(entity.GetType());
            EnsureEntityHasPrimaryKey(typeDescriptor);

            MappingMemberDescriptor keyMemberDescriptor = typeDescriptor.PrimaryKey;
            MemberInfo keyMember = typeDescriptor.PrimaryKey.MemberInfo;

            var keyVal = keyMemberDescriptor.GetValue(entity);

            if (keyVal == null)
                throw new ChloeException(string.Format("The primary key '{0}' could not be null.", keyMember.Name));

            DbTable dbTable = table == null ? typeDescriptor.Table : new DbTable(table, typeDescriptor.Table.Schema);
            DbExpression left = new DbColumnAccessExpression(dbTable, keyMemberDescriptor.Column);
            DbExpression right = new DbParameterExpression(keyVal);
            DbExpression conditionExp = new DbEqualExpression(left, right);

            DbDeleteExpression e = new DbDeleteExpression(dbTable, conditionExp);
            return this.ExecuteSqlCommand(e);
        }
        public virtual int Delete<TEntity>(Expression<Func<TEntity, bool>> condition)
        {
            return this.Delete(condition, null);
        }
        public virtual int Delete<TEntity>(Expression<Func<TEntity, bool>> condition, string table)
        {
            Utils.CheckNull(condition);

            TypeDescriptor typeDescriptor = TypeDescriptor.GetDescriptor(typeof(TEntity));

            DbTable explicitDbTable = null;
            if (table != null)
                explicitDbTable = new DbTable(table, typeDescriptor.Table.Schema);
            DefaultExpressionParser expressionParser = typeDescriptor.GetExpressionParser(explicitDbTable);
            DbExpression conditionExp = expressionParser.ParseFilterPredicate(condition);

            DbDeleteExpression e = new DbDeleteExpression(explicitDbTable ?? typeDescriptor.Table, conditionExp);

            return this.ExecuteSqlCommand(e);
        }
        public virtual int DeleteByKey<TEntity>(object key)
        {
            return this.DeleteByKey<TEntity>(key, null);
        }
        public virtual int DeleteByKey<TEntity>(object key, string table)
        {
            Expression<Func<TEntity, bool>> predicate = BuildPredicate<TEntity>(key);
            return this.Delete<TEntity>(predicate, table);
        }


        public virtual void TrackEntity(object entity)
        {
            Utils.CheckNull(entity);
            Type entityType = entity.GetType();

            if (ReflectionExtension.IsAnonymousType(entityType))
                return;

            Dictionary<Type, TrackEntityCollection> entityContainer = this.TrackingEntityContainer;

            TrackEntityCollection collection;
            if (!entityContainer.TryGetValue(entityType, out collection))
            {
                TypeDescriptor typeDescriptor = TypeDescriptor.GetDescriptor(entityType);

                if (!typeDescriptor.HasPrimaryKey())
                    return;

                collection = new TrackEntityCollection(typeDescriptor);
                entityContainer.Add(entityType, collection);
            }

            collection.TryAddEntity(entity);
        }
        protected virtual string GetSelectLastInsertIdClause()
        {
            return "SELECT @@IDENTITY";
        }
        protected virtual IEntityState TryGetTrackedEntityState(object entity)
        {
            Utils.CheckNull(entity);
            Type entityType = entity.GetType();
            Dictionary<Type, TrackEntityCollection> entityContainer = this._trackingEntityContainer;

            if (entityContainer == null)
                return null;

            TrackEntityCollection collection;
            if (!entityContainer.TryGetValue(entityType, out collection))
            {
                return null;
            }

            IEntityState ret = collection.TryGetEntityState(entity);
            return ret;
        }

        public void Dispose()
        {
            if (this._disposed)
                return;

            if (this._adoSession != null)
                this._adoSession.Dispose();
            this.Dispose(true);
            this._disposed = true;
        }
        protected virtual void Dispose(bool disposing)
        {

        }
        void CheckDisposed()
        {
            if (this._disposed)
            {
                throw new ObjectDisposedException(this.GetType().FullName);
            }
        }


        int ExecuteSqlCommand(DbExpression e)
        {
            IDbExpressionTranslator translator = this.DbContextServiceProvider.CreateDbExpressionTranslator();
            List<DbParam> parameters;
            string cmdText = translator.Translate(e, out parameters);

            int r = this.AdoSession.ExecuteNonQuery(cmdText, parameters.ToArray(), CommandType.Text);
            return r;
        }

        static Expression<Func<TEntity, bool>> BuildPredicate<TEntity>(object key)
        {
            Utils.CheckNull(key);

            Type entityType = typeof(TEntity);
            TypeDescriptor typeDescriptor = TypeDescriptor.GetDescriptor(entityType);
            EnsureEntityHasPrimaryKey(typeDescriptor);

            ParameterExpression parameter = Expression.Parameter(entityType, "a");
            Expression propOrField = Expression.PropertyOrField(parameter, typeDescriptor.PrimaryKey.MemberInfo.Name);
            Expression keyValue = Chloe.Extensions.ExpressionExtension.MakeWrapperAccess(key, typeDescriptor.PrimaryKey.MemberInfoType);
            Expression lambdaBody = Expression.Equal(propOrField, keyValue);

            Expression<Func<TEntity, bool>> predicate = Expression.Lambda<Func<TEntity, bool>>(lambdaBody, parameter);

            return predicate;
        }
        static void EnsureEntityHasPrimaryKey(TypeDescriptor typeDescriptor)
        {
            if (!typeDescriptor.HasPrimaryKey())
                throw new ChloeException(string.Format("The entity type '{0}' does not define a primary key.", typeDescriptor.EntityType.FullName));
        }
        static object ConvertIdentityType(object identity, Type conversionType)
        {
            if (identity.GetType() != conversionType)
                return Convert.ChangeType(identity, conversionType);

            return identity;
        }
        static List<Tuple<JoinType, Expression>> ResolveJoinInfo(LambdaExpression joinInfoExp)
        {
            NewArrayExpression body = joinInfoExp.Body as NewArrayExpression;

            List<Tuple<JoinType, Expression>> ret = new List<Tuple<JoinType, Expression>>();

            if ((joinInfoExp.Parameters.Count - 1) * 2 != body.Expressions.Count)
            {
                throw new ArgumentException(string.Format("Invalid join infomation '{0}'.", joinInfoExp));
            }

            for (int i = 0; i < joinInfoExp.Parameters.Count - 1; i++)
            {
                int indexOfJoinType = i * 2;

                Expression joinTypeExpression = body.Expressions[indexOfJoinType];
                object inputJoinType = ExpressionEvaluator.Evaluate(joinTypeExpression);
                if (inputJoinType.GetType() != typeof(JoinType))
                    throw new ArgumentException(string.Format("Not support '{0}',please input correct type of 'Chloe.JoinType'.", joinTypeExpression));

                Expression joinCondition = body.Expressions[indexOfJoinType + 1].StripConvert();

                if (joinCondition.Type != typeof(bool))
                {
                    throw new ArgumentException(string.Format("Not support '{0}',please input correct join condition.", joinCondition));
                }

                ParameterExpression[] parameters = joinInfoExp.Parameters.Take(i + 2).ToArray();

                List<Type> typeArguments = parameters.Select(a => a.Type).ToList();
                typeArguments.Add(typeof(bool));

                Type delegateType = Utils.GetFuncDelegateType(typeArguments.ToArray());

                LambdaExpression newLambda = Expression.Lambda(delegateType, joinCondition, parameters);

                ret.Add(new Tuple<JoinType, Expression>((JoinType)inputJoinType, newLambda));
            }

            return ret;
        }

        class TrackEntityCollection
        {
            public TrackEntityCollection(TypeDescriptor typeDescriptor)
            {
                this.TypeDescriptor = typeDescriptor;
                this.Entities = new Dictionary<object, IEntityState>(1);
            }
            public TypeDescriptor TypeDescriptor { get; private set; }
            public Dictionary<object, IEntityState> Entities { get; private set; }
            public bool TryAddEntity(object entity)
            {
                if (this.Entities.ContainsKey(entity))
                {
                    return false;
                }

                IEntityState entityState = new EntityState(this.TypeDescriptor, entity);
                this.Entities.Add(entity, entityState);

                return true;
            }
            public IEntityState TryGetEntityState(object entity)
            {
                IEntityState ret;
                if (!this.Entities.TryGetValue(entity, out ret))
                    ret = null;

                return ret;
            }
        }
    }
}
