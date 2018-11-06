﻿using System;
using System.Collections.Generic;

namespace Shareable
{
    public class STransaction :SDatabase 
    {
        // uids above this number are for uncommitted objects
        public static readonly long _uid = 0x80000000;
        public readonly long uid;
        public readonly bool autoCommit;
        public readonly SDatabase rollback;
        public readonly SDict<int,Serialisable> steps;
        public STransaction(SDatabase d,bool auto) :base(d)
        {
            autoCommit = auto;
            rollback = (d is STransaction t)?t.rollback:d;
            uid = _uid;
            steps = SDict<int,Serialisable>.Empty;
        }
        public STransaction(STransaction tr,Serialisable s) :base(tr.Add(s,tr.uid+1))
        {
            autoCommit = tr.autoCommit;
            rollback = tr.rollback;
            steps = tr.steps.Add(tr.steps.Count,s);
            uid =  tr.uid+1;
        }
        public STransaction(STransaction tr,int c) :base(tr)
        {
            autoCommit = tr.autoCommit;
            rollback = tr.rollback;
            steps = tr.steps;
            uid = tr.uid;
        }
        public SDatabase Commit()
        {
            AStream dbfile = dbfiles.Lookup(name);
            SDatabase db = databases.Lookup(name);
            long pos = 0;
            var since = dbfile.GetAll(this, curpos, db.curpos);
            for (var i = 0; i < since.Length; i++)
                for (var b = steps.First(); b != null; b = b.Next())
                    if (since[i].Conflicts(b.Value.val))
                        throw new Exception("Transaction Conflict on " + b.Value);
            lock (dbfile.file)
            {
                db = databases.Lookup(name);
                since = dbfile.GetAll(this, pos,dbfile.Length);
                for (var i = 0; i < since.Length; i++)
                    for (var b = steps.First(); b != null; b = b.Next())
                        if (since[i].Conflicts(b.Value.val))
                            throw new Exception("Transaction Conflict on " + b.Value);
                db = dbfile.Commit(db,steps);
            }
            Install(db);
            return db;
        }
        /// <summary>
        /// We will single-quote transaction-local uids
        /// </summary>
        /// <returns>a more readable version of the uid</returns>
        internal static string Uid(long uid)
        {
            if (uid > _uid)
                return "'" + (uid - _uid);
            return "" + uid;
        }
 /*       protected override SDatabase Install(STable t,long p)
        {
            return new STransaction(this,t,p);
        }
        protected override SDatabase Install(SColumn c,long p)
        {
            return new STransaction(this,((STable)Lookup(c.table)).Add(c),p);
        }
        protected override SDatabase Install(SRecord r,long p)
        {
            return new STransaction(this,((STable)Lookup(r.table)).Add(r),p);
        }
        protected override SDatabase Install(SDelete d, long p)
        {
            return new STransaction(this,((STable)Lookup(d.table)).Remove(d.delpos),p);
        }
        protected override SDatabase Install(SAlter a, long p)
        {
            return new STransaction(this, a, p);
        }
        protected override SDatabase Install(SDrop d, long p)
        {
            return new STransaction(this, d, p);
        }
        protected override SDatabase Install(SView v, long p)
        {
            return new STransaction(this, v, p);
        } */
        public override STransaction Transact(bool auto=true)
        {
            return this; // ignore the parameter
        }
        public override SDatabase Rollback()
        {
            return rollback;
        }
    }
}
