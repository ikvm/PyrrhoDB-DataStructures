﻿using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Net;
using System.Net.Sockets;
using Shareable;
namespace StrongLink
{
    public class ServerException : Exception
    {
        public ServerException(string message) : base(message) { }
    }
    public enum IndexType { Primary =0, Unique=1, Reference=2 };
    public class StrongConnect
    {
        internal ClientStream asy;
        public bool inTransaction = false;
        SDict<long, string> preps = SDict<long, string>.Empty;
        public SDict<int, string>? description = null; // see ExecuteQuery
        public string lastreq;
        public StrongConnect(string host, int port, string fn)
        {
            Socket? socket = null;
            try
            {
                IPEndPoint ep;
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                if (char.IsDigit(host[0]))
                {
                    IPAddress ip = IPAddress.Parse(host);
                    ep = new IPEndPoint(ip, port);
                    socket.Connect(ep);
                }
                else
                {
#if MONO1
                    var he = Dns.GetHostByName(hostName);
#else
                    IPHostEntry he = Dns.GetHostEntry(host);
#endif
                    for (int j = 0; j < he.AddressList.Length; j++)
                        try
                        {
                            IPAddress ip = he.AddressList[j];
                            ep = new IPEndPoint(ip, port);
                            socket.Connect(ep);
                            if (socket.Connected)
                                break;
                        }
                        catch (Exception) { }
                }
            }
            catch (Exception)
            {
            }
            if (socket == null || !socket.Connected)
                throw new Exception("No connection to " + host + ":" + port);
            asy = new ClientStream(this, socket);
            var wtr = asy.wtr;
            wtr.PutString(fn);
            asy.Flush();
            asy.rdr.ReadByte();
            preps = SDict<long, string>.Empty;
        }
        public long Prepare(string n)
        {
            var u = -(preps.Length ?? 0) - 2;
            preps += (u, n);
            return u;
        }
        public void CreateTable(string n)
        {
            var un = Prepare(n);
            var wtr = asy.wtr;
            wtr.SendUids(preps);
            wtr.Write(Types.SCreateTable);
            wtr.PutLong(un);
            wtr.PutInt(0);
            wtr.PutInt(0);
            var b = asy.Receive();
            preps = SDict<long,string>.Empty;
        }
        public void CreateColumn(string c,Types t,string tn,params (string,SFunction)[] constraints)
        {
            var uc = Prepare(c);
            var ut = Prepare(tn);
            asy.wtr.SendUids(preps);
            asy.wtr.Write(Types.SCreateColumn);
            new SColumn(uc, t, ut, new SDict<string, SFunction>(constraints)).PutColDef(asy.wtr);
            var b = asy.Receive();
            preps = SDict<long,string>.Empty;
        }
        public void CreateIndex(string tn,IndexType t,string? rt,params string[] key)
        {
            var ut = Prepare(tn);
            long u = -1;
            if (rt!=null)
                u=Prepare(rt);
            var keys = SList<long>.Empty;
            for (var i = key.Length-1;i>=0; i--)
                keys += Prepare(key[i]);
            asy.wtr.SendUids(preps);
            new SIndex(ut, t == IndexType.Primary,u, keys).Put(asy.wtr);
            var b = asy.Receive();
            preps = SDict<long,string>.Empty;
        }
        public void Insert(string tn,string[] cols,params Serialisable[][] rows)
        {
            var ut = Prepare(tn);
            var u = new long[cols.Length];
            for (var i = 0; i < cols.Length; i++)
                u[i] = Prepare(cols[i]);
            var wtr = asy.wtr;
            wtr.SendUids(preps);
            wtr.Write(Types.Insert);
            wtr.PutLong(ut);
            wtr.PutInt(cols.Length);
            // insert cols if supplied
            for (var i = 0; i < cols.Length;i++)
                wtr.PutLong(u[i]);
            // now the rows
            wtr.PutInt(rows[0].Length);
            wtr.PutInt(rows.Length);
            for (var i = 0; i < rows.Length; i++)
                for (var j = 0; j < rows[i].Length; j++)
                    rows[i][j].Put(asy.wtr);
            var b = asy.Receive();
            preps = SDict<long,string>.Empty;
        }
        public DocArray ExecuteQuery(string sql)
        {
            lastreq = sql;
            var pair = Parser.Parse(sql);
            var qry = pair.Item1 as SQuery;
            if (qry == null)
                throw new Exception("Bad query " + sql);
            return Get(pair.Item2,qry);
        }
        public Types ExecuteNonQuery(string sql)
        {
            asy.rdr.buf.len = 0;
            lastreq = sql;
            var s = Parser.Parse(sql);
            if (s.Item2 == null)
                return Types.Exception;
            var wtr = asy.wtr;
            wtr.SendUids(s.Item2);
            s.Item1.Put(wtr);
            var b = asy.Receive();
            if (b == Types.Exception)
                inTransaction = false;
            else
            {
                var su = sql.Trim().Substring(0, 5).ToUpper();
                switch (su)
                {
                    case "BEGIN": inTransaction = true; break;
                    case "ROLLB":
                    case "COMMI": inTransaction = false; break;
                }
            }
            return b;
        }
        public DocArray Get(SDict<long,string> d,Serialisable tn)
        {
            asy.rdr.buf.len = 0;
            var wtr = asy.wtr;
            wtr.SendUids(d);
            wtr.Write(Types.DescribedGet);
            tn.Put(wtr);
            asy.Flush();
            var b = asy.rdr.ReadByte();
            if (b == (byte)Types.Exception)
            {
                inTransaction = false;
                asy.rdr.GetException();
            }
            if (b == (byte)Types.Done)
            {
                description = SDict<int, string>.Empty;
                var n = asy.rdr.GetInt();
                for (var i = 0; i < n; i++)
                    description += (i, asy.rdr.GetString());
                return new DocArray(asy.rdr.GetString());
            }
            throw new Exception("PE28");
        }
        public void BeginTransaction()
        {
            asy.rdr.buf.len = 0;
            asy.wtr.Write(Types.SBegin);
            var b = asy.Receive();
            if (b == Types.Exception)
            {
                inTransaction = false;
                asy.rdr.GetException();
            }
            if (b == Types.Done)
                inTransaction = true;
        }
        public void Rollback()
        {
            asy.rdr.buf.len = 0;
            asy.wtr.Write(Types.SRollback);
            var b = asy.Receive();
            inTransaction = false;
        }
        public void Commit()
        {
            asy.rdr.buf.len = 0;
            asy.wtr.Write(Types.SCommit);
            var b = asy.Receive();
            inTransaction = false;
        }
        public void ExecuteNonQuery(SDict<long,string> d,Serialisable s)
        {
            asy.rdr.buf.len = 0;
            asy.wtr.SendUids(d);
            s.Put(asy.wtr);
            var b = asy.Receive();
            if (b == Types.Exception)
                inTransaction = false;
            else
                switch (s.type)
                {
                    case Types.SBegin: inTransaction = true; break;
                    case Types.SRollback:
                    case Types.SCommit: inTransaction = false; break;
                }
        } 
        public void Close()
        {
            asy.Close();
        }
    }
    /// <summary>
    /// not shareable
    /// </summary>
    class ClientStream : Stream
    {
        internal Socket client;
        static long _cid = 0;
        long cid = ++_cid;
        internal int rx = 0;
        internal ClientReader rdr;
        internal ClientWriter wtr;
        internal ClientStream(StrongConnect pc, Socket c)
        {
            client = c;
            wtr = new ClientWriter(client);
            rdr = new ClientReader(client);
            rdr.buf.pos = 2;
            rdr.buf.len = 0;
        }
        public Types Receive()
        {
            if (wtr.buf.pos > 2)
                wtr.PutBuf();
            rdr.buf.pos = 2;
            rdr.buf.len = 0;
            return (Types)rdr.ReadByte();
        }

        public override void Flush()
        {
            rdr.buf.pos = 2;
            rdr.buf.len = 0;
            try
            {
                wtr.PutBuf();
                wtr.buf.pos = 2;
            }
            catch (SocketException e)
            {
                Console.WriteLine("Flush reports exception " + e.Message);
                throw e;
            }
        }
        public override bool CanRead
        {
            get { return true; }
        }
        public override bool CanWrite
        {
            get { return true; }
        }
        public override bool CanSeek
        {
            get { return false; }
        }
        public override long Length
        {
            get => 0;
        }
        public override long Position
        {
            get
            {
                throw new Exception("The method or operation is not implemented.");
            }
            set
            {
                throw new Exception("The method or operation is not implemented.");
            }
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new Exception("The method or operation is not implemented.");
        }
        public override void SetLength(long value)
        {
            throw new Exception("The method or operation is not implemented.");
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }
    }
    public class ClientReader:SocketReader
    {
        bool getting;
        public ClientReader(Socket c) : base(c) { } 
        public override bool GetBuf(long p) // parameter is ignored
        {
            getting = true;
            int rcount;
            try
            {
                var rc = client.Receive(buf.buf, Buffer.Size, 0);
                if (rc == 0)
                {
                    rcount = 0;
                    getting = false;
                    return false;
                }
                rcount = (buf.buf[0] << 7) + buf.buf[1];
                buf.len = rcount + 2;
                if (rcount == Buffer.Size - 1)
                    GetException();
                getting = false;
                return rcount > 0;
            }
            catch (SocketException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
        // v2.0 exception handling during server comms
        // an illegal nonzero rcount value indicates an exception
        internal int GetException()
        {
            var rcount = (buf.buf[buf.pos++] << 7) + (buf.buf[buf.pos++] & 0x7f);
            buf.len = rcount + 4;
            var b = buf.buf[buf.pos++];
            if (b != (byte)Types.Exception)
                throw new Exception("PE30");
            var em = GetString();
    //        Console.WriteLine("Received exception: " + em);
            throw new ServerException(em);
        }
    }
    public class ClientWriter:SocketWriter
    {
        public ClientWriter(Socket c) : base(c) { } 
        public void SendUids(SDict<long, string> u)
        {
            Write(Types.SNames);
            PutInt(u.Length);
            for (var b = u.First(); b != null; b = b.Next())
            {
                PutLong(b.Value.Item1);
                PutString(b.Value.Item2);
            }
        }
        public void SendUids(params (string, long)[] u)
        {
            Write(Types.SNames);
            PutInt(u.Length);
            for (var i = 0; i < u.Length; i++)
            {
                PutString(u[i].Item1);
                PutLong(u[i].Item2);
            }
        }
    }
}
