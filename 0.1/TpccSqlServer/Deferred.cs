using System;
using System.Collections;
using System.Data.SqlClient;
using System.Threading;

namespace Tpcc
{
	/// <summary>
	/// Summary description for Deferred.
	/// </summary>
	public class Deferred
	{
		public SqlConnection db;
		public int wid;

		public Deferred(int w)
		{
            db = new SqlConnection("Data Source=MALCOLM1;Initial Catalog=Tpcc;Integrated Security=True;Pooling=False");
            db.Open();
            wid = w;
		}

		bool Schedule(int did,int carid,SqlTransaction tr)
		{
			int oid = 0;
			int ocid = 0;
            var cmd = db.CreateCommand();
            cmd.Transaction = tr;
			cmd.CommandText = "select NO_O_ID from NEW_ORDER where NO_W_ID="+wid+" and NO_D_ID="+did;
            var s = cmd.ExecuteReader();
            if (!s.Read())
            {
                s.Close();
                return false;
            }
			oid = (int)s[0];
            s.Close();
            cmd.CommandText ="delete NEW_ORDER where NO_W_ID="+wid+" and NO_D_ID="+did+" and NO_O_ID="+oid;
            cmd.ExecuteNonQuery();
			cmd.CommandText="select O_C_ID from ORDER where O_W_ID="+wid+" and O_D_ID="+did+" and O_ID="+oid;
            s = cmd.ExecuteReader();
            s.Read();
		    ocid = (int)s[0];
            s.Close();
            cmd.CommandText="update [ORDER] where O_W_ID="+wid+" and O_D_ID="+did+" and O_ID="+oid + " set O_CARRIER_ID = "+carid;
            cmd.ExecuteNonQuery();
			cmd.CommandText = "update ORDER_LINE  where OL_W_ID="+wid+" and OL_D_ID="+did+" and OL_O_ID="+oid+ " set OL_DELIVERY_DATE='" + DateTime.Now.ToString("o") + "'";
            cmd.ExecuteNonQuery();
            decimal amount = 0.0M;
			cmd.CommandText = "select sum(OL_AMOUNT) from ORDER_LINE where OL_W_ID="+wid+" and OL_D_ID="+did+" and OL_O_ID="+oid;
            s = cmd.ExecuteReader();
            s.Read();
		    amount = util.GetDecimal(s[0]);
            s.Close();
			cmd.CommandText = "update CUSTOMER  where C_W_ID=" + wid + " and C_D_ID=" + did + " and C_ID=" + ocid+" set C_BALANCE =C_BALANCE+"+amount+",C_DELIVERY_CNT=C_DELIVERY_CNT+1";
            s = cmd.ExecuteReader();
            return true;
		}

        void Carrier(int carid)
        {
            int done = 0, skipped = 0;
            var tr = db.BeginTransaction(System.Data.IsolationLevel.Serializable);
            for (int d = 1; d <= 10; d++)
                if (Schedule(d, carid, tr))
                    done++;
                else
                    skipped++;
            tr.Commit();
            Form1.commits++;
        }

		public void Run()
		{
			ArrayList al = new ArrayList();
            for (; ; )
            {
                var cmd = db.CreateCommand();
                cmd.CommandText="select DL_CARRIER_ID from DELIVERY where DL_W_ID=" + wid + " and DL_DONE is null order by DL_ID";
                var s = cmd.ExecuteReader();
                while(s.Read())
                    al.Add((int)s[0]);
                s.Close();
                foreach (int k in al)
                    Carrier(k);
                Thread.Sleep(30000); // 30 sec
            }
		}
	}
}
