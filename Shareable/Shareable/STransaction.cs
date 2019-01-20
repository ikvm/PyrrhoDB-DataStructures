﻿using System;
#nullable enable

namespace Collection
{
    public class STransaction :SDatabase 
    {
        // uids above this number are for uncommitted objects in this transaction
        // Note: uncommitted objects of any type are added to tr.objects 
        public static readonly long _uid = 0x4000000000000000;
        public readonly long uid;
        public readonly bool autoCommit;
        public readonly SDatabase rollback;
        internal override SDatabase _Rollback => rollback;
        protected override bool Committed => false;
        public STransaction(SDatabase d,bool auto) :base(d)
        {
            autoCommit = auto;
            rollback = d._Rollback;
            uid = _uid;
        }
        /// <summary>
        /// Some other set of updates to existing (and maybe named) objects 
        /// </summary>
        /// <param name="tr"></param>
        /// <param name="obs"></param>
        /// <param name="nms"></param>
        protected STransaction(STransaction tr, SDict<long,SDbObject> obs,SDict<string,SDbObject> nms,long c)
            : base(tr, obs, nms, c)
        {
            autoCommit = tr.autoCommit;
            rollback = tr.rollback;
            uid = tr.uid + 1;
        }
        protected override SDatabase New(SDict<long, SDbObject> o, SDict<string, SDbObject> ns, long c)
        {
            return new STransaction(this,o, ns, c);
        }
        protected override Serialisable _Get(long pos)
        {
            if (pos < 0 || pos >= _uid)
                return objects[pos];
            return base._Get(pos);
        }
        public SDatabase Commit()
        {
            var f = dbfiles[name];
            SDatabase db = databases[name];
            var rdr = new Reader(f, curpos);
            var since = rdr.GetAll(db,db.curpos);
            for (var i = 0; i < since.Length; i++)
                for (var b = objects.PositionAt(_uid); b != null; b = b.Next())
                    if (since[i].Conflicts(b.Value.val))
                        throw new Exception("Transaction Conflict on " + b.Value);
            lock (f)
            {
                since = rdr.GetAll(this, f.length);
                for (var i = 0; i < since.Length; i++)
                    for (var b = objects.PositionAt(_uid); b != null; b = b.Next())
                        if (since[i].Conflicts(b.Value.val))
                            throw new Exception("Transaction Conflict on " + b.Value);
                db = f.Commit(db,this);
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
            return SDbObject._Uid(uid);
        }
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
