﻿using System.IO;
namespace Shareable
{
    public class SDatabase
    {
        public readonly string name;
        public readonly SDict<long, SDbObject> objects;
        public readonly SDict<string, SDbObject> names;
        public readonly long curpos;
        static object files = new object(); // a lock
        protected static SDict<string,AStream> dbfiles = SDict<string,AStream>.Empty;
        protected static SDict<string, SDatabase> databases = SDict<string,SDatabase>.Empty;
        public static SDatabase Open(string fname)
        {
            if (dbfiles.Contains(fname))
                return databases.Lookup(fname);
            var db = new SDatabase(fname);
            lock (files)
            {
                dbfiles = dbfiles.Add(fname, new AStream(fname));
                databases = databases.Add(fname, db);
            }
            return db.Load();
        }
        SDatabase(string fname)
        {
            name = fname;
            objects = SDict<long, SDbObject>.Empty;
            names = SDict<string, SDbObject>.Empty;
            curpos = 0;
        }
        protected SDatabase(SDatabase db)
        {
            name = db.name;
            objects = db.objects;
            names = db.names;
            curpos = db.curpos;
        }
        protected SDatabase(SDatabase db,long p)
        {
            name = db.name;
            objects = db.objects.Remove(p);
            names = db.names;
            curpos = db.curpos;
        }
        protected SDatabase(SDatabase db,STable t,long c)
        {
            name = db.name;
            objects = db.objects.Add(t.uid, t);
            names = db.names.Add(t.name, t);
            curpos = c;
        }
        protected SDatabase(SDatabase db,SAlter a,long c)
        {
            name = db.name;
            if (a.parent==0)
            {
                var ot = (STable)db.objects.Lookup(a.defpos);
                var nt = new STable(ot,a.name);
                objects = db.objects.Add(a.defpos, nt);
                names = db.names.Remove(ot.name).Add(a.name,nt);
            } else
            {
                var ot = (STable)db.objects.Lookup(a.parent);
                var oc = ot.cols.Lookup(a.defpos);
                var nc = new SColumn(oc, a.name, a.dataType);
                var nt = ot.Add(nc);
                objects = db.objects.Add(a.defpos, nt);
                names = db.names.Add(a.name, nt);
            }
            curpos = c;
        }
        protected SDatabase(SDatabase db,SDrop d,long c)
        {
            name = db.name;
            if (d.parent == 0)
            {
                var ot = (STable)db.objects.Lookup(d.drpos);
                objects = db.objects.Remove(d.drpos);
                names = names.Remove(ot.name);
            } else { 
                var ot = (STable)db.objects.Lookup(d.parent);
                var nt = ot.Remove(d.drpos);
                objects = db.objects.Add(d.parent, nt);
            }
            curpos = c;
        }
        protected SDatabase(SDatabase db,SView v,long c)
        {
            name = db.name;
            objects = objects.Add(v.uid, v);
            names = names.Add(v.name, v);
            curpos = c;
        }
        protected SDatabase(SDatabase db,SIndex x,long c)
        {
            name = db.name;
            objects = objects.Add(x.uid, x);
        }
        SDatabase Load()
        {
            var f = dbfiles.Lookup(name);
            var db = this;
            lock (f)
            {
                for (var s = f.GetOne(this); s != null; s = f.GetOne(this))
                    db = db.Add(s,f.Position);
            }
            return db;
        }
        public SDatabase Add(Serialisable s,long p)
        {
            switch (s.type)
            {
                case Types.STable: return Install((STable)s, p); 
                case Types.SColumn: return Install((SColumn)s, p); 
                case Types.SUpdate:
                case Types.SRecord: return Install((SRecord)s, p); 
                case Types.SDelete: return Install((SDelete)s, p); 
                case Types.SAlter: return Install((SAlter)s, p); 
                case Types.SDrop: return Install((SDrop)s, p); 
                case Types.SView: return Install((SView)s, p); 
            }
            return this;
        }
        public SDatabase Remove(long p)
        {
            return new SDatabase(this, p);
        }
        /// <summary>
        /// Only for testing environments!
        /// </summary>
        public void Close()
        {
            lock(files)
            {
                var f = dbfiles.Lookup(name);
                databases = databases.Remove(name);
                dbfiles = dbfiles.Remove(name);
                f.Close();
            }
        }
        protected virtual SDatabase Install(STable t,long c)
        {
            return new SDatabase(this, t, c);
        }
        protected virtual SDatabase Install(SColumn c,long p)
        {
            return new SDatabase(this,((STable)objects.Lookup(c.table)).Add(c),p);
        }
        protected virtual SDatabase Install(SRecord r,long c)
        {
            return new SDatabase(this, ((STable)objects.Lookup(r.table)).Add(r),c);
        }
        protected virtual SDatabase Install(SDelete d,long c)
        {
            return new SDatabase(this, ((STable)objects.Lookup(d.table)).Remove(d.delpos),c);
        }
        protected virtual SDatabase Install(SAlter a,long c)
        {
            return new SDatabase(this, a, c);
        }
        protected virtual SDatabase Install(SDrop d, long c)
        {
            return new SDatabase(this, d, c);
        }
        protected virtual SDatabase Install(SView v, long c)
        {
            return new SDatabase(this, v, c);
        }
        protected virtual SDatabase Install(SIndex x,long c)
        {
            return new SDatabase(this, x, c);
        }
        public virtual STransaction Transact()
        {
            return new STransaction(this);
        }
        public virtual STransaction MaybeAutoCommit(STransaction tr)
        {
            return tr.Commit();
        }
        public virtual SDatabase Rollback()
        {
            return this;
        }
    }
}
