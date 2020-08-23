using Pyrrho.Common;
using Pyrrho.Level3;
using Pyrrho.Level4;

// Pyrrho Database Engine by Malcolm Crowe at the University of the West of Scotland
// (c) Malcolm Crowe, University of the West of Scotland 2004-2020
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
	/// A View definition
	/// </summary>
    internal class PView : Physical
    {
        /// <summary>
        /// The name of the View
        /// </summary>
        public string name;
        /// <summary>
        /// The definition of the view
        /// </summary>
        public QueryExpression view;
        public override long Dependent(Writer wr, Transaction tr)
        {
            return -1;
        }
        /// <summary>
        /// Constructor: A view definition from the Parser
        /// </summary>
        /// <param name="tp">The PView type</param>
        /// <param name="nm">The name of the view</param>
        /// <param name="vc">The definition of the view</param>
        /// <param name="pb">The physical database</param>
        /// <param name="curpos">The current position in the datafile</param>
        internal PView(string nm, QueryExpression vc, long pp, Context cx)
            : this(Type.PView, nm, vc, pp, cx) { }
        protected PView(Type pt,string nm,QueryExpression vc,long pp, Context cx) 
            : base(pt,pp,cx)
        {
            name = nm;
            view = vc;
        }
        /// <summary>
        /// Constructor: A view definition from the buffer
        /// </summary>
        /// <param name="bp">The buffer</param>
        /// <param name="pos">The defining position</param>
        public PView(Reader rdr) : base(Type.PView, rdr) { }
        protected PView(Type tp, Reader rdr) : base(tp, rdr) { }
        protected PView(PView x, Writer wr) : base(x, wr)
        {
            name = x.name;
            view = x.view;
        }
        protected override Physical Relocate(Writer wr)
        {
            return new PView(this, wr);
        }

        /// <summary>
        /// Serialise this Physical to the PhysBase
        /// </summary>
        /// <param name="r">Relocation information for positions</param>
        public override void Serialise(Writer wr)
        {
            wr.PutString(name.ToString());
            wr.PutString(view.ToString());
            base.Serialise(wr);
        }
        /// <summary>
        /// deserialise this Physical from the buffer
        /// </summary>
        /// <param name="buf">the buffer</param>
        public override void Deserialise(Reader rdr)
        {
            name = rdr.GetString();
            view = new Parser(rdr.context).ParseQueryExpression(rdr.GetString(),Domain.Content);
            base.Deserialise(rdr);
        }
        /// <summary>
        /// a readable version of this Physical
        /// </summary>
        /// <returns>the string representation</returns>
        public override string ToString()
        {
            return "View " + name + " as " + view.ToString();
        }
        public override DBException Conflicts(Database db, Context cx, Physical that,PTransaction ct)
        {
            switch(that.type)
            {
                case Type.PTable1:
                case Type.PTable:
                    if (name == ((PTable)that).name)
                        return new DBException("40030", ppos, that, ct);
                    break;
                case Type.PView1:
                case Type.PView:
                case Type.RestView1:
                case Type.RestView2:
                case Type.RestView:
                    if (name == ((PView)that).name)
                        return new DBException("40012", ppos, that, ct);
                    break;
                case Type.Change:
                    if (name == ((Change)that).name)
                        return new DBException("40032", ppos, that, ct);
                    break;
            }
            return base.Conflicts(db, cx, that, ct);
        }
        internal override void Install(Context cx, long p)
        {
            var ro = cx.db.role;
            var vi = new ObInfo(ppos, name, Domain.Content);
            ro = ro+vi+(ppos,vi);
            cx.db += (ro, p);
            cx.Install(new View(this), p);
            cx.db += (Database.Log, cx.db.log + (ppos, type));
        }
    }
    internal class PRestView : PView
    {
        internal long structpos,usingtbpos;
        internal string rname = null, rpass = null;
        public PRestView(Reader rdr) : this(Type.RestView, rdr) { }
        protected PRestView(Type t, Reader rdr) : base(t,rdr) { }
        public PRestView(string nm, long tp, long pp, Context cx)
            : this(Type.RestView, nm, tp, pp, cx) { }
        protected PRestView(Type t,string nm,long tp,long pp, Context cx)
            : base(t,nm,QueryExpression.Get,pp,cx)
        {
            structpos = tp;
        }
        protected PRestView(PRestView x, Writer wr) : base(x, wr)
        {
            structpos = wr.Fix(x.structpos);
        }
        protected override Physical Relocate(Writer wr)
        {
            return new PRestView(this, wr);
        }

        public override void Serialise(Writer wr)
        {

            wr.PutLong(structpos);
            base.Serialise(wr);
        }
        public override void Deserialise(Reader rdr)
        {
            structpos = rdr.GetLong();
            base.Deserialise(rdr);
        }
        public override string ToString()
        {
            return "PRestView "+name + "("+structpos+")";
        }
    }
    /// <summary>
    /// This class is deprecated: credentials information can be safely provided in URL
    /// </summary>
    internal class PRestView1 : PRestView
    {
        public PRestView1(Reader rdr) : base(Type.RestView1, rdr) { }
        public PRestView1(string nm, long tp, string rnm, string rpw, long pp, 
            Context cx) : base(Type.RestView1, nm, tp, pp, cx)
        {
            rname = rnm;
            rpass = rpw;
        }
        protected PRestView1(PRestView1 x, Writer wr) : base(x, wr)
        {
            rname = x.rname;
            rpass = x.rpass;
        }
        protected override Physical Relocate(Writer wr)
        {
            return new PRestView1(this, wr);
        }

        public override void Serialise(Writer wr)
        {
            wr.PutString(rname);
            wr.PutString(rpass);
            base.Serialise(wr);
        }
        public override void Deserialise(Reader rdr)
        {
            rname = rdr.GetString();
            rpass = rdr.GetString();
            base.Deserialise(rdr);
        }
        public override string ToString()
        {
            return "PRestView1 " + name + "(" + structpos + ") '" +rname+"':'"+rpass +"'";
        }
    }
    internal class PRestView2 : PRestView
    {
        public PRestView2(Reader rdr) : base(Type.RestView1, rdr) { }
        public PRestView2(string nm, long tp, long utp, long pp, Context cx)
            : base(Type.RestView2, nm, tp, pp, cx)
        {
            usingtbpos = utp;
        }
        protected PRestView2(PRestView2 x, Writer wr) : base(x, wr)
        {
            usingtbpos = wr.Fix(x.usingtbpos);
        }
        protected override Physical Relocate(Writer wr)
        {
            return new PRestView2(this, wr);
        }

        public override void Serialise(Writer wr)
        {
            wr.PutLong(usingtbpos);
            base.Serialise(wr);
        }
        public override void Deserialise(Reader rdr)
        {
            usingtbpos = rdr.GetLong();
            base.Deserialise(rdr);
        }
        public override DBException Conflicts(Database db, Context cx, Physical that, PTransaction ct)
        {
            if (that.type == Type.Drop && usingtbpos == ((Drop)that).delpos)
                return new DBException("40012",usingtbpos, that, ct);
            return base.Conflicts(db, cx, that, ct);
        }
        public override string ToString()
        {
            return "PRestView2 " + name + "(" + structpos + ") using " + usingtbpos;
        }
    }

    /// <summary>
    /// This class is obsolete: deserialisation of View1 definitions from a database file is supported for backward compatibility
    /// </summary>
    internal class PView1 : PView
    {
        /// <summary>
        /// Constructor: A view definition from the buffer
        /// </summary>
        /// <param name="bp">The buffer</param>
        /// <param name="pos">The defining position</param>
        public PView1(Reader rdr) : base(Type.PView1,rdr) { }
        protected PView1(PView1 x, Writer wr) : base(x, wr) { }
        protected override Physical Relocate(Writer wr)
        {
            return new PView1(this, wr);
        }

        /// <summary>
        /// deserialise this Physical from the buffer
        /// </summary>
        /// <param name="buf">the buffer</param>
        public override void Deserialise(Reader rdr)
        {
            rdr.GetString();
            rdr.GetString();
            rdr.GetString();
            base.Deserialise(rdr);
        }
    }
}
