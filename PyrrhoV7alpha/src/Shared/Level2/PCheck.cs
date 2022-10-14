using System;
using Pyrrho.Common;
using Pyrrho.Level3;
using Pyrrho.Level4;

// Pyrrho Database Engine by Malcolm Crowe at the University of the West of Scotland
// (c) Malcolm Crowe, University of the West of Scotland 2004-2022
//
// This software is without support and no liability for damage consequential to use.
// You can view and test this code, and use it subject for any purpose.
// You may incorporate any part of this code in other software if its origin 
// and authorship is suitably acknowledged.
// All other use or distribution or the construction of any product incorporating 
// this technology requires a license from the University of the West of Scotland.
namespace Pyrrho.Level2
{
	/// <summary>
	/// A PCheck is for a check constraint for Table, Column, or Domain.
	/// </summary>
	internal class PCheck : Compiled
	{
		public long ckobjdefpos; // of object (e.g. Domain,Table) to which this check applies
        public long subobjdefpos = -1; // of Column if a columns check
		public string name;
        public long defpos;
		public string check;
        public long test;
        public override long Dependent(Writer wr, Transaction tr)
        {
            if (!Committed(wr,ckobjdefpos)) return ckobjdefpos;
            if (!Committed(wr,subobjdefpos)) return subobjdefpos;
            if (defpos!=ppos && !Committed(wr,defpos)) return defpos;
            return -1;
        }
        /// <summary>
        /// Constructor: A new check constraint from the Parser
        /// </summary>
        /// <param name="dm">The object to which the check applies</param>
        /// <param name="nm">The name of the constraint</param>
        /// <param name="cs">The constraint as a string</param>
        /// <param name="db">The local database</param>
        public PCheck(long dm, string nm, SqlValue se, string cs, long pp, Context cx)
            : this(Type.PCheck, dm, nm, se, cs, pp, cx) { }
        protected PCheck(Type tp, long dm, string nm, SqlValue se, string cs, 
            long pp, Context cx) : base(tp,pp,cx,dm,Domain.Bool)
		{
			ckobjdefpos = dm;
            defpos = ppos;
            name = nm ?? throw new DBException("42102");
			check = cs;
            test = se.defpos;
        }
        /// <summary>
        /// Constructor: A new check constraint from the buffer
        /// </summary>
        /// <param name="bp">The buffer</param>
        /// <param name="pos">The defining position</param>
		public PCheck(Reader rdr) : base (Type.PCheck,rdr)
		{}
        protected PCheck(PCheck x, Writer wr) : base(x, wr)
        {
            ckobjdefpos = wr.cx.Fix(x.ckobjdefpos);
            defpos = wr.cx.Fix(x.defpos);
            name = x.name;
            check = x.check;
            test = x.test;
        }
        protected override Physical Relocate(Writer wr)
        {
            return new PCheck(this, wr);
        }
        /// <summary>
        /// A readable version of this Physical
        /// </summary>
        /// <returns>the string representation</returns>
		public override string ToString()
		{
			return "Check " +name+" ["+Pos(ckobjdefpos)+"]: "+check;
		}
        /// <summary>
        /// Serialise this Physical to the PhysBase
        /// </summary>
        /// <param name="r">Relocation information for positions</param>
        public override void Serialise(Writer wr)
		{
            wr.PutLong(ckobjdefpos);
            wr.PutString(name.ToString());
            wr.PutString(check);
			base.Serialise(wr);
		}
        /// <summary>
        /// Deserialise this Physical from the buffer
        /// </summary>
        /// <param name="buf">the buffer</param>
        public override void Deserialise(Reader rdr)
		{
			ckobjdefpos = rdr.GetLong();
			name = rdr.GetString();
            defpos = ppos;
			var src = rdr.GetIdent();
            check = src.ident;
			base.Deserialise(rdr);
        }
        internal override void OnLoad(Reader rdr)
        {
            if (check != "")
            {
                var ob = ((DBObject)rdr.context.db.objects[ckobjdefpos]);
                var psr = new Parser(rdr, new Ident(check, rdr.context.Ix(ppos+1)), ob);
                nst = psr.cx.db.nextStmt;
                var sv = psr.ParseSqlValue(Domain.Bool).Reify(rdr.context);
                test = sv.defpos;
                framing = new Framing(psr.cx,nst);
            }
        }
        public override DBException Conflicts(Database db, Context cx, Physical that, PTransaction ct)
        {
            switch(that.type)
            {
                case Type.PCheck2:
                case Type.PCheck:
                    if (name!="" && name == ((PCheck)that).name)
                        return new DBException("40046", defpos, that, ct);
                    break;
                case Type.Drop:
                    if (ckobjdefpos == ((Drop)that).delpos)
                        return new DBException("40010", defpos, that, ct);
                    break;
                case Type.Change:
                    if (ckobjdefpos == ((Change)that).affects)
                        return new DBException("40025", defpos, that, ct);
                    break;
                case Type.Alter:
                    if (ckobjdefpos == ((Alter)that).defpos)
                        return new DBException("40025", defpos, that, ct);
                    break;
            }
            return base.Conflicts(db, cx, that, ct);
        }
        internal override void Install(Context cx, long p)
        {
            var ro = cx.db.role;
            var ck = new Check(this, cx.db);
            if (name != null && name != "")
                ck += (DBObject.Infos, new BTree<long, ObInfo>(ro.defpos,
                    new ObInfo(name, Grant.Privilege.Execute)));
            if (cx.db.mem.Contains(Database.Log))
                cx.db += (Database.Log, cx.db.log + (ppos, type));
            cx.Install(((DBObject)cx.db.objects[ck.checkobjpos]).Add(ck, cx.db),p);
            cx.Install(ck,p);
            base.Install(cx, p);
        }
        public override (Transaction,Physical) Commit(Writer wr, Transaction t)
        {
            var (tr,ph) = base.Commit(wr, t);
            var pc = (PCheck)ph;
            var ck = (DBObject)tr.objects[defpos] + (Check.Condition, pc.framing.obs[pc.test])
                + (DBObject._Framing, pc.framing);
            var co = ((DBObject)tr.objects[ckobjdefpos]).Add((Check)ck, tr);
            wr.cx.instDFirst = -1;
            return ((Transaction)(tr + (ck, tr.loadpos)+(co,tr.loadpos)),ph);
        }
    }
    /// <summary>
    /// A version of PCheck that deals with deeply-structured objects.
    /// </summary>
    internal class PCheck2 : PCheck
    {
      /// <summary>
        /// Constructor: A new check constraint from the Parser
        /// </summary>
        /// <param name="dm">The object to which the check applies</param>
        /// <param name="so">The subobject to which the check applies</param>
        /// <param name="nm">The name of the constraint</param>
        /// <param name="cs">The constraint as a string</param>
        /// <param name="pb">The local database</param>
        public PCheck2(long dm, long so, string nm, SqlValue se, string cs, long pp, 
            Context cx)
            : base(Type.PCheck2,dm,nm,se,cs,pp,cx)
		{
            subobjdefpos=so;
		}
        /// <summary>
        /// Constructor: A new check constraint from the buffer
        /// </summary>
        /// <param name="bp">The buffer</param>
        /// <param name="pos">The defining position</param>
		public PCheck2(Reader rdr) : base(rdr)
		{}
        protected PCheck2(PCheck2 p, Writer wr) : base(p, wr) 
        {
            subobjdefpos = wr.cx.Fix(p.subobjdefpos);
        }
        protected override Physical Relocate(Writer wr)
        {
            return new PCheck2(this,wr);
        }
        /// <summary>
        /// A readable version of this Physical
        /// </summary>
        /// <returns>the string representation</returns>
		public override string ToString()
		{
			return "Check " +name+" ["+Pos(ckobjdefpos)+":"+Pos(subobjdefpos)+"]: "+check;
		}
        /// <summary>
        /// Serialise this Physical to the PhysBase
        /// </summary>
        /// <param name="r">Relocation information for positions</param>
        public override void Serialise(Writer wr)
		{
			subobjdefpos = wr.cx.Fix(subobjdefpos);
            wr.PutLong(subobjdefpos);
			base.Serialise(wr);
		}
        /// <summary>
        /// Deserialise this Physical from the buffer
        /// </summary>
        /// <param name="buf">the buffer</param>
        public override void Deserialise(Reader rdr)
		{
			subobjdefpos = rdr.GetLong();
			base.Deserialise(rdr);
		}
        /// <summary>
        /// Looks the same as PCheck::Install but gets a different constructor for Check
        /// and calls a different Add !
        /// </summary>
        /// <param name="db"></param>
        /// <param name="ro"></param>
        /// <param name="p"></param>
        /// <returns></returns>
        internal override void Install(Context cx, long p)
        {
            var ro = cx.db.role;
            var ck = new Check(this, cx.db);
            cx.Install(ck,p);
            var nc = ((DBObject)cx.db.objects[ck.checkobjpos]).Add(ck, cx.db);
            cx.Install(nc,p);
            // we don't install this new column in ck's framing, as there is
            // no good way to maintain the surrounding context reliably in the framing
            if (name != null && name != "")
            {
                nc += (DBObject.Infos, new BTree<long, ObInfo>(ro.defpos,
                    new ObInfo(name, Grant.Privilege.Execute)));
                cx.db += (nc, p);
            }
            if (cx.db.mem.Contains(Database.Log))
                cx.db += (Database.Log, cx.db.log + (ppos, type));
        }
    }
}
