using System;
using System.Text;
using Pyrrho.Common;
using Pyrrho.Level1;
using Pyrrho.Level4;
using Pyrrho.Level3;
using System.Security.Principal;

// Pyrrho Database Engine by Malcolm Crowe at the University of the West of Scotland
// (c) Malcolm Crowe, University of the West of Scotland 2004-2019
//
// This software is without support and no liability for damage consequential to use
// You can view and test this code 
// All other use or distribution or the construction of any product incorporating this technology 
// requires a license from the University of the West of Scotland
namespace Pyrrho.Level2
{
	/// <summary>
	/// A Role definition
	/// </summary>
	internal class PRole: Physical
	{
        /// <summary>
        /// The name of the Role
        /// </summary>
		public string name;
        /// <summary>
        /// The description of the role
        /// </summary>
        public string details = "";
        public override long Dependent(Writer wr)
        {
            return -1;
        }
        /// <summary>
        /// Constructor: a Role definition from the Parser
        /// </summary>
        /// <param name="nm">The name of the role</param>
        /// <param name="dt">The description of the role</param>
        /// <param name="wh">The physical database</param>
        /// <param name="curpos">The position in the datafile</param>
		public PRole(string nm,string dt,long u, Transaction tr):base(Type.PRole,u,tr)
		{
            name = nm;
            details = dt;
        }
        public PRole(Reader rdr) : base(Type.PRole, rdr) { }
        protected PRole(PRole x, Writer wr) : base(x, wr)
        {
            name = x.name;
            details = x.details;
        }
        protected override Physical Relocate(Writer wr)
        {
            return new PRole(this, wr);
        }
        /// <summary>
        /// Serialise this Physical to the PhysBase
        /// </summary>
        /// <param name="r">Relocation information for positions</param>
        public override void Serialise(Writer wr)
		{
            wr.PutString(name.ToString());
            if (type==Type.PRole)
                wr.PutString(details);
			base.Serialise(wr);
		}
        /// <summary>
        /// Deserialise this Physical from the buffer
        /// </summary>
        /// <param name="buf">the buffer</param>
        public override void Deserialise(Reader rdr) 
		{
            name = rdr.GetString();
            if (type == Type.PRole)
                details = rdr.GetString();
			base.Deserialise(rdr);
		}
        public override long Conflicts(Database db, Transaction tr, Physical that)
        {
            switch(that.type)
            {
                case Type.PRole:
                    return (name==((PRole)that).name) ? ppos : -1;
                case Type.Change:
                    return (name == ((Change)that).name) ? ppos : -1;
            }
            return base.Conflicts(db, tr, that);
        }
        /// <summary>
        /// A readable version of this Physical
        /// </summary>
        /// <returns>the string representation</returns>
		public override string ToString() { return "PRole "+name; }

        internal override Database Install(Database db, Role ro, long p)
        {
            // If this is the first Role to be defined, 
            // it is given all the SchemaRole's objects
            var first = true;
            for (var b = db.roles.PositionAt(0); first && b != null; b = b.Next())
                if (!(b.value() is User))
                    first = false;
            var nr = new Role(this, db, first);
            db += nr;
            if (first)
            { // install as the new schema role and drop the old one
                var sr = db.schemaRole;
                db += (Database.Schema, nr.defpos);
            }
            // The new Role can be seen publicly but usage is controlled
            return db + (db.schemaRole, nr);
        }
    }
     internal class PMetadata : Physical
     {
         public string name = null;
         /// <summary>
         /// column sequence number for view column
         /// </summary>
        public long seq;
        public long defpos;
        public string description = "";
        public string iri = "";
        public long refpos;
        public ulong flags = 0;
        static Sqlx[] keys = new Sqlx[] { Sqlx.ENTITY, Sqlx.ATTRIBUTE, Sqlx.REFERS, Sqlx.REFERRED,
            Sqlx.PIE, Sqlx.POINTS, Sqlx.X, Sqlx.Y, Sqlx.HISTOGRAM, Sqlx.LINE,
            Sqlx.CAPTION, Sqlx.LEGEND, Sqlx.JSON, Sqlx.CSV };
        public override long Dependent(Writer wr)
        {
            if (defpos!=ppos && !Committed(wr,defpos)) return defpos;
            if (!Committed(wr,refpos)) return refpos;
            return -1;
        }
        /// <summary>
        /// Constructor: role-based metadata from the parser
        /// </summary>
        /// <param name="nm">The name of the object</param>
        /// <param name="md">The new metadata</param>
        /// <param name="sq">The column seq no for a view column</param>
        /// <param name="ob">the DBObject ref</param>
        /// <param name="wh">The physical database</param>
        /// <param name="curpos">The position in the datafile</param>
        public PMetadata(string nm, long sq, long ob, string ds,string i,
            long rf,ulong fl,long u,Transaction tr)
          :this(Type.Metadata,nm,sq,ob,ds,i,rf,fl,u,tr)
		{
        }
        public PMetadata(string nm, Metadata md, long pp, long dp, long u,Transaction tr)
            :this(Type.Metadata,nm,md.seq,dp,md.description,md.iri,md.refpos,md.flags,u,tr)
        { }
        protected PMetadata(Type t, string nm, long sq, long ob, string ds, 
            string i,long rf,ulong fl,long u,Transaction tr)
          : base(t, u, tr)
        {
            name = nm;
            seq = sq;
            defpos = ob;
            description = ds;
            iri = i;
            refpos = rf;
            flags = fl;
        }
        public PMetadata(Reader rdr) : this(Type.Metadata, rdr) { }
        protected PMetadata(Type t, Reader rdr) : base(t, rdr) { }
        protected PMetadata(PMetadata x, Writer wr) : base(x, wr)
        {
            name = x.name;
            seq = x.seq;
            defpos = wr.Fix(defpos);
            description = x.description;
            iri = x.iri;
            refpos = wr.Fix(x.refpos);
            flags = x.flags;
        }
        protected override Physical Relocate(Writer wr)
        {
            return new PMetadata(this, wr);
        }
        /// <summary>
        /// Serialise this Physical to the PhysBase
        /// </summary>
        /// <param name="r">Relocation information for positions</param>
        public override void Serialise(Writer wr)
		{
            wr.PutString(name.ToString());
            wr.PutString(description);
            wr.PutString(iri);
            defpos = wr.Fix(defpos);
            wr.PutLong(seq+1); 
            wr.PutLong(defpos);
            wr.PutLong((long)flags);
			base.Serialise(wr);
		}
        /// <summary>
        /// Deserialise this Physical from the buffer
        /// </summary>
        /// <param name="buf">the buffer</param>
        public override void Deserialise(Reader rdr) 
		{
			name =rdr.GetString();
            description = rdr.GetString();
            iri = rdr.GetString();
            seq = rdr.GetLong()-1;
            defpos = rdr.GetLong();
            flags = (ulong)rdr.GetLong();
            base.Deserialise(rdr);
		}
        /// <summary>
        /// A readable version of this Physical
        /// </summary>
        /// <returns>the string representation</returns>
		public override string ToString() 
        {
            return "PMetadata "+name + "["+defpos+((seq>=0)?("."+seq):"")+"]" 
                + ((description!="")?"(" + description +")":"")+
                iri + Flags();
        }
        public override long Conflicts(Database db, Transaction tr, Physical that)
        {
            switch(that.type)
            {
                case Type.Metadata3:
                case Type.Metadata2:
                case Type.Metadata:
                    {
                        var t = (PMetadata)that;
                        return (defpos == t.defpos || name == t.name) ? ppos : -1;
                    }
                case Type.Drop:
                    return (((Drop)that).delpos == defpos) ? ppos : -1;
            }
            return base.Conflicts(db, tr, that);
        }
        internal void Add(Sqlx k)
        {
            ulong m = 1;
            for (int i = 0; i < keys.Length; i++, m = m * 2)
                if (k == keys[i])
                    flags |= m;
        }
        internal void Drop(Sqlx k)
        {
            ulong m = 1;
            for (int i = 0; i < keys.Length; i++, m = m * 2)
                if (k == keys[i])
                    flags &= ~m;
        }
        internal bool Has(Sqlx k)
        {
            ulong m = 1;
            for (int i = 0; i < keys.Length; i++, m = m * 2)
                if (k == keys[i] && (flags & m) != 0)
                    return true;
            return false;
        }
        internal string Flags()
        {
            var sb = new StringBuilder();
            ulong m = 1;
            for (int i = -0; i < keys.Length; i++, m = m * 2)
                if ((flags & m) != 0)
                    sb.Append(" " + keys[i]);
            return sb.ToString();
        }

        internal override Database Install(Database db, Role ro, long p)
        {
            var ob = (DBObject)ro.objects[defpos];
            for (var b = ob.Add(this, db).First(); b != null; b = b.Next())
                db += (ro,b.value());
            return db;
        }
    }
     internal class PMetadata2 : PMetadata
     {
       /// <summary>
        /// Constructor: role-based metadata from the parser
        /// </summary>
        /// <param name="nm">The name of the object</param>
        /// <param name="md">The new metadata</param>
        /// <param name="sq">The column seq no for a view column</param>
        /// <param name="ob">the DBObject ref</param>
        /// <param name="db">The physical database</param>
         public PMetadata2(string nm, long sq, long ob, string ds, string i,
            long rf, ulong fl, long u,Transaction tr)
          :this(Type.Metadata2,nm,sq,ob,ds,i,rf,fl,u,tr)
		{
        }
        protected PMetadata2(Type tp,string nm, long sq, long ob, string ds, string i,
           long rf, ulong fl, long u,Transaction tr)
         : base(tp, nm, sq, ob, ds, i, rf, fl, u,tr)
        {
        }
        public PMetadata2(Reader rdr) : base (Type.Metadata2,rdr){}
        public PMetadata2(Type pt,Reader rdr) : base(pt, rdr) {}
        protected PMetadata2(PMetadata2 x, Writer wr) : base(x, wr)
        {
        }
        protected override Physical Relocate(Writer wr)
        {
            return new PMetadata2(this, wr);
        }
        /// <summary>
        /// Serialise this Physical to the PhysBase
        /// </summary>
        /// <param name="r">Relocation information for positions</param>
        public override void Serialise(Writer wr)
		{
			base.Serialise(wr);
		}
        /// <summary>
        /// Deserialise this Physical from the buffer
        /// </summary>
        /// <param name="buf">the buffer</param>
        public override void Deserialise(Reader rdr) 
		{
            rdr.GetInt();
            rdr.GetLong();
			base.Deserialise(rdr);
		}
       /// <summary>
        /// A readable version of this Physical
        /// </summary>
        /// <returns>the string representation</returns>
		public override string ToString() 
        {
            return "PMetadata2 " + name + "[" + defpos + "." + seq + "]" + ((description != "") ? "(" + description + ")" : "") +
                iri + Flags();
        }

     }
    internal class PMetadata3 : PMetadata2
    {
        /// <summary>
        /// Constructor: role-based metadata from the parser
        /// </summary>
        /// <param name="nm">The name of the object</param>
        /// <param name="md">The new metadata</param>
        /// <param name="sq">The column seq no for a view column</param>
        /// <param name="ob">the DBObject ref</param>
        /// <param name="wh">The physical database</param>
        /// <param name="curpos">The position in the datafile</param>
        public PMetadata3(string nm, long sq, long ob, string ds, string i,
            long rf, ulong fl, long u,Transaction tr)
         : base(Type.Metadata3, nm, sq, ob, ds,i,rf,fl,u,tr)
        {
        }
        public PMetadata3(Reader rdr) : base(Type.Metadata3, rdr) { }
        protected PMetadata3(PMetadata3 x, Writer wr) : base(x, wr)
        {
        }
        protected override Physical Relocate(Writer wr)
        {
            return new PMetadata3(this, wr);
        }
        /// <summary>
        /// Serialise this Physical to the PhysBase
        /// </summary>
        /// <param name="r">Relocation information for positions</param>
        public override void Serialise(Writer wr)
        {
            wr.PutLong(refpos);
            base.Serialise(wr);
        }
        /// <summary>
        /// Deserialise this Physical from the buffer
        /// </summary>
        /// <param name="buf">the buffer</param>
        public override void Deserialise(Reader rdr)
        {
            refpos = rdr.GetLong();
            base.Deserialise(rdr);
        }
        /// <summary>
        /// A readable version of this Physical
        /// </summary>
        /// <returns>the string representation</returns>
        public override string ToString()
        {
            return "PMetadata3 " + name + "[" + defpos + "." + seq + "]" + ((description != "") ? "(" + description + ")" : "") +
                iri + Flags() + DBObject.Uid(refpos);
        }

    }
}