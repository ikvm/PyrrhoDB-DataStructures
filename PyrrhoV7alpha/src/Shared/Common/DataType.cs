using System;
using System.CodeDom;
using System.Runtime.ExceptionServices;
using System.Text;
using Pyrrho.Level3;
// Pyrrho Database Engine by Malcolm Crowe at the University of the West of Scotland
// (c) Malcolm Crowe, University of the West of Scotland 2004-2021
//
// This software is without support and no liability for damage consequential to use.
// You can view and test this code, and use it subject for any purpose.
// You may incorporate any part of this code in other software if its origin 
// and authorship is suitably acknowledged.
// All other use or distribution or the construction of any product incorporating 
// this technology requires a license from the University of the West of Scotland.
/// <summary>
/// Everything in the Common namespace is Immutable and Shareabl
/// </summary>
namespace Pyrrho.Common
{
    /// <summary>
    /// Sqlx enumerates the tokens of SQL2011, mostly defined in the standard
    /// The order is only roughly alphabetic
    /// </summary>
    public enum Sqlx
    {
        Null = 0,
        // reserved words (not case sensitive) SQL2011 vol2, vol4, vol14 + // entries
        // 3 alphabetical sequences: reserved words, token types, and non-reserved words
        ///===================RESERVED WORDS=====================
        // last reserved word must be YEAR (mentioned in code below); XML is a special case
        ABS = 1,
        ALL = 2,
        ALLOCATE = 3,
        ALTER = 4,
        AND = 5,
        ANY = 6,
        ARE = 7, // ARRAY see 11
        ARRAY_AGG = 8,
        ARRAY_MAX_CARDINALITY = 9,
        AS = 10,
        ARRAY = 11, // must be 11
        ASENSITIVE = 12,
        ASYMMETRIC = 13,
        ATOMIC = 14,
        AUTHORIZATION = 15,
        AVG = 16, //
        BEGIN = 17,
        BEGIN_FRAME = 18,
        BEGIN_PARTITION = 19,
        BETWEEN = 20,
        BIGINT = 21,
        BINARY = 22,
        BLOB = 23, // BOOLEAN see 27
        BOTH = 24,
        BY = 25,
        CALL = 26,
        BOOLEAN = 27, // must be 27
        CALLED = 28,
        CARDINALITY = 29,
        CASCADED = 30,
        CASE = 31,
        CAST = 32,
        CEIL = 33,
        CEILING = 34, // CHAR see 37
        CHAR_LENGTH = 35,
        CHARACTER = 36,
        CHAR = 37, // must be 37: see also CHARLITERAL for literal 
        CHARACTER_LENGTH = 38,
        CHECK = 39, // CLOB see 40
        CLOB = 40, // must be 40
        CLOSE = 41,
        COALESCE = 42,
        COLLATE = 43,
        COLLECT = 44,
        COLUMN = 45,
        COMMIT = 46,
        CONDITION = 47,
        CONNECT = 48,
        CONSTRAINT = 49,
        CONTAINS = 50,
        CONVERT = 51,
#if OLAP
        CORR =52,
#endif
        CORRESPONDING = 53,
        COUNT = 54, //
#if OLAP
        COVAR_POP =55,
        COVAR_SAMP =56,
#endif
        CREATE = 57,
        CROSS = 58,
#if OLAP
        CUBE =59,
        CUME_DIST =60,
#endif
        CURRENT = 61,
        CURRENT_CATALOG = 62,
        CURRENT_DATE = 63,
        CURRENT_DEFAULT_TRANSFORM_GROUP = 64,
        CURSOR = 65, // must be 65
        CURRENT_PATH = 66,
        DATE = 67, // must be 67	
        CURRENT_ROLE = 68,
        CURRENT_ROW = 69,
        CURRENT_SCHEMA = 70,
        CURRENT_TIME = 71,
        CURRENT_TIMESTAMP = 72,
        CURRENT_TRANSFORM_GROUP_FOR_TYPE = 73,
        CURRENT_USER = 74, // CURSOR see 65
        CYCLE = 75, // DATE see 67
        DAY = 76,
        DEALLOCATE = 77,
        DEC = 78,
        DECIMAL = 79,
        DECLARE = 80,
        DEFAULT = 81,
        DELETE = 82,
#if OLAP
        DENSE_RANK =83,
#endif
        DEREF = 84,
        DESCRIBE = 85,
        DETERMINISTIC = 86,
        DISCONNECT = 87,
        DISTINCT = 88,
        DO = 89, // from vol 4
        DOCARRAY = 90, // Pyrrho 5.1
        DOCUMENT = 91, // Pyrrho 5.1
        DOUBLE = 92,
        DROP = 93,
        DYNAMIC = 94,
        EACH = 95,
        ELEMENT = 96,
        ELSE = 97,
        ELSEIF = 98, // from vol 4
        END = 99,
        END_EXEC = 100, // misprinted in SQL2011 as END-EXEC
        END_FRAME = 101,
        END_PARTITION = 102,
        EOF = 103,	// Pyrrho 0.1
        EQUALS = 104,
        ESCAPE = 105,
        EVERY = 106,
        EXCEPT = 107,
        EXEC = 108,
        EXECUTE = 109,
        EXISTS = 110,
        EXIT = 111, // from vol 4
        EXP = 112,
        EXTERNAL = 113,
        EXTRACT = 114,
        FALSE = 115,
        FETCH = 116,
        FILTER = 117,
        FIRST_VALUE = 118,
        FLOAT = 119,
        FLOOR = 120,
        FOR = 121,
        FOREIGN = 122,
        FREE = 123,
        FROM = 124,
        FULL = 125,
        FUNCTION = 126,
        FUSION = 127,
        GET = 128,
        GLOBAL = 129,
        GRANT = 130,
        GROUP = 131,
        GROUPING = 132,
        GROUPS = 133,
        HANDLER = 134, // vol 4 
        INT = 136,  // deprecated: see also INTEGERLITERAL
        INTEGER = 135, // must be 135
        HAVING = 138,
        INTERVAL0 = 137,  // must be 137 (old version of INTERVAL)
        HOLD = 139,
        HOUR = 140,
        IDENTITY = 141,
        IF = 142,  // vol 4
        IN = 143,
        INDICATOR = 144,
        INNER = 145,
        INOUT = 146,
        INSENSITIVE = 147,
        INSERT = 148, // INT is 136, INTEGER is 135
        INTERSECT = 149,
        INTERSECTION = 150,
        INTO = 151,
        IS = 152,
        INTERVAL = 153, // must be 152 see also INTERVAL0
        ITERATE = 154, // vol 4
        JOIN = 155,
        LAG = 156,
        LANGUAGE = 157,
        LARGE = 158,
        LAST_DATA = 159, // Pyrrho v7
        LAST_VALUE = 160,
        LATERAL = 161,
        LEADING = 162,
        LEAVE = 163, // vol 4
        LEFT = 164,
        LIKE = 165,
        LN = 166,
        LOCAL = 167,
        MULTISET = 168, // must be 168
        LOCALTIME = 169,
        LOCALTIMESTAMP = 170,
        NCHAR = 171, // must be 171	
        NCLOB = 172, // must be 172
        LOOP = 173,  // vol 4
        LOWER = 174,
        MATCH = 175,
        MAX = 176, 
        NULL = 177, // must be 177
        MEMBER = 178,
        NUMERIC = 179, // must be 179
        MERGE = 180,
        METHOD = 181,
        MIN = 182, 
        MINUTE = 183,
        MOD = 184,
        MODIFIES = 185,
        MODULE = 186,
        MONTH = 187,	 // MULTISET is 168
        NATIONAL = 188,
        NATURAL = 189, // NCHAR 171 NCLOB 172
        NEW = 190,
        NO = 191,
        NONE = 192,
        NORMALIZE = 193,
        NOT = 194,
        NULLIF = 195, // NUMERIC 179, see also NUMERICLITERAL
        OBJECT = 196,
        OCCURRENCES_REGEX = 197, // alphabetical sequence is a misprint in SQL2008
        OCTET_LENGTH = 198,
        REAL0 = 199, // must be 199, previous version of REAL
        OF = 200,
        OFFSET = 201,
        OLD = 202,
        REAL = 203, // must be 203, see also REAL0 and REALLITERAL
        ON = 204,
        ONLY = 205,
        OPEN = 206,
        OR = 207,
        ORDER = 208,
        OUT = 209,
        OUTER = 210,
        OVER = 211,
        OVERLAPS = 212,
        OVERLAY = 213,
        PARAMETER = 214,
        PARTITION = 215,
        PERCENT = 216,
        PERIOD = 217,
        PASSWORD = 218, // must be 218, Pyrrho v5
        PORTION = 219,
        POSITION = 220,
        POWER = 221,
        PRECEDES = 222,
        PRECISION = 223,
        PREPARE = 224,
        PRIMARY = 225,
        PROCEDURE = 226,
        RANGE = 227,
        RANK = 228,
        READS = 229,    // REAL 203 (previously 199 see REAL0)
        RECURSIVE = 230,
        REF = 231,
        REFERENCES = 232,
        REFERENCING = 233,
        RELEASE = 234,
        REPEAT = 235, // vol 4
        RESIGNAL = 236, // vol 4
        RESULT = 237,
        RETURN = 238,
        RETURNS = 239,
        REVOKE = 240,
        RIGHT = 241,
        ROLLBACK = 242,
        ROW = 243,
        ROW_NUMBER = 244,
        ROWS = 245,
        SAVEPOINT = 246,
        SCOPE = 247,
        SCROLL = 248,
        SEARCH = 249,
        SECOND = 250,
        SELECT = 251,
        SENSITIVE = 252,    // has a different usage in Pyrrho
        SESSION_USER = 253,
        SET = 254,
        SIGNAL = 255, //vol 4
        SMALLINT = 256,
        TIME = 257, // must be 257
        TIMESTAMP = 258, // must be 258
        SOME = 259,
        SPECIFIC = 260,
        SPECIFICTYPE = 261,
        SQL = 262,
        SQLEXCEPTION = 263,
        SQLSTATE = 264,
        SQLWARNING = 265,
        SQRT = 266,
        TYPE = 267, // must be 267 but is not a reserved word
        START = 268,
        STATIC = 269,
        STDDEV_POP = 270,
        STDDEV_SAMP = 271,
        SUBMULTISET = 272,
        SUBSTRING = 273, //
        SUBSTRING_REGEX = 274,
        SUCCEEDS = 275,
        SUM = 276, //
        SYMMETRIC = 277,
        SYSTEM = 278,
        SYSTEM_TIME = 279,
        SYSTEM_USER = 280,  // TABLE is 297
        TABLESAMPLE = 281,
        THEN = 282,  // TIME is 257, TIMESTAMP is 258	
        TIMEZONE_HOUR = 283,
        TIMEZONE_MINUTE = 284,
        TO = 285,
        TRAILING = 286,
        TRANSLATE = 287,
        TRANSLATION = 288,
        TREAT = 289,
        TRIGGER = 290,
        TRIM = 291,
        TRIM_ARRAY = 292,
        TRUE = 293,
        TRUNCATE = 294,
        UESCAPE = 295,
        UNION = 296,
        TABLE = 297, // must be 297
        UNIQUE = 298,
        UNKNOWN = 299,
        UNNEST = 300,
        UNTIL = 301, // vol 4
        UPDATE = 302,
        UPPER = 303, //
        USER = 304,
        USING = 305,
        VALUE = 306,
        VALUE_OF = 307,
        VALUES = 308,
        VARBINARY = 309,
        VARCHAR = 310,
        VARYING = 311,
        VERSIONING = 312,
        WHEN = 313,
        WHENEVER = 314,
        WHERE = 315,
        WHILE = 316, // vol 4
        WINDOW = 317,
        WITH = 318,
        WITHIN = 319,
        WITHOUT = 320, // XML is 356 vol 14
        XMLAGG = 321, // Basic XML stuff + XMLAgg vol 14
        XMLATTRIBUTES = 322,
        XMLBINARY = 323,
        XMLCAST = 324,
        XMLCOMMENT = 325,
        XMLCONCAT = 326,
        XMLDOCUMENT = 327,
        XMLELEMENT = 328,
        XMLEXISTS = 329,
        XMLFOREST = 330,
        XMLITERATE = 331,
        XMLNAMESPACES = 332,
        XMLPARSE = 333,
        XMLPI = 334,
        XMLQUERY = 335,
        XMLSERIALIZE = 336,
        XMLTABLE = 337,
        XMLTEXT = 338,
        XMLVALIDATE = 339,
        YEAR = 340,	// last reserved word
        //====================TOKEN TYPES=====================
        ASSIGNMENT = 341, // := 
        BLOBLITERAL = 342, // 
        BOOLEANLITERAL = 343,
        CHARLITERAL = 344, //
        COLON = 345, // :
        COMMA = 346,  // ,        
        CONCATENATE = 347, // ||        
        DIVIDE = 348, // /        
        DOCUMENTLITERAL = 349, // v5.1
        DOT = 350, // . 5.2 was STOP
        DOUBLECOLON = 351, // ::        
        EMPTY = 352, // []        
        EQL = 353,  // =        
        GEQ = 354, // >=    
        GTR = 355, // >    
        XML = 356, // must be 356 (is a reserved word)
        ID = 357, // identifier
        INTEGERLITERAL = 358, // Pyrrho
        LBRACE = 359, // {
        LBRACK = 360, // [
        LEQ = 361, // <=
        LPAREN = 362, // (
        LSS = 363, // <
        MEXCEPT = 364, // 
        MINTERSECT = 365, //
        MINUS = 366, // -
        MUNION = 367, // 
        NEQ = 368, // <>
        NUMERICLITERAL = 369, // 
        PLUS = 370, // + 
        QMARK = 371, // ?
        RBRACE = 372, // } 
        RBRACK = 373, // ] 
        RDFDATETIME = 374, //
        RDFLITERAL = 375, // 
        RDFTYPE = 376, // Pyrrho 7.0
        REALLITERAL = 377, //
        RPAREN = 378, // ) 
        SEMICOLON = 379, // ; 
        TIMES = 380, // *
        VBAR = 381, // | 
        //=========================NON-RESERVED WORDS================
        A = 382, // first non-reserved word
        ABSOLUTE = 383,
        ACTION = 384,
        ADA = 385,
        ADD = 386,
        ADMIN = 387,
        AFTER = 388,
        ALWAYS = 389,
        APPLICATION = 390, // Pyrrho 4.6
        ASC = 391,
        ASSERTION = 392,
        ATTRIBUTE = 393,
        ATTRIBUTES = 394,
        BEFORE = 395,
        BERNOULLI = 396,
        BREADTH = 397,
        BREAK = 398, // Pyrrho
        C = 399,
        CAPTION = 400, // Pyrrho 4.5
        CASCADE = 401,
        CATALOG = 402,
        CATALOG_NAME = 403,
        CHAIN = 404,
        CHARACTER_SET_CATALOG = 405,
        CHARACTER_SET_NAME = 406,
        CHARACTER_SET_SCHEMA = 407,
        CHARACTERISTICS = 408,
        CHARACTERS = 409,
        CLASS_ORIGIN = 410,
        COBOL = 411,
        COLLATION = 412,
        COLLATION_CATALOG = 413,
        COLLATION_NAME = 414,
        COLLATION_SCHEMA = 415,
        COLUMN_NAME = 416,
        COMMAND_FUNCTION = 417,
        COMMAND_FUNCTION_CODE = 418,
        COMMITTED = 419,
        CONDITION_NUMBER = 420,
        CONNECTION = 421,
        CONNECTION_NAME = 422,
        CONSTRAINT_CATALOG = 423,
        CONSTRAINT_NAME = 424,
        CONSTRAINT_SCHEMA = 425,
        CONSTRAINTS = 426,
        CONSTRUCTOR = 427,
        CONTENT = 428,
        CONTINUE = 429,
        CSV = 430, // Pyrrho 5.5
        CURATED = 431, // Pyrrho
        CURSOR_NAME = 432,
        DATA = 433,
        DATABASE = 434, // Pyrrho
        DATETIME_INTERVAL_CODE = 435,
        DATETIME_INTERVAL_PRECISION = 436,
        DEFAULTS = 437,
        DEFERRABLE = 438,
        DEFERRED = 439,
        DEFINED = 440,
        DEFINER = 441,
        DEGREE = 442,
        DEPTH = 443,
        DERIVED = 444,
        DESC = 445,
        DESCRIPTOR = 446,
        DIAGNOSTICS = 447,
        DISPATCH = 448,
        DOMAIN = 449,
        DYNAMIC_FUNCTION = 450,
        DYNAMIC_FUNCTION_CODE = 451,
        ENFORCED = 452,
        ENTITY = 453, // Pyrrho 4.5
        EXCLUDE = 454,
        EXCLUDING = 455,
        FINAL = 456,
        FIRST = 457,
        FLAG = 458, 
        FOLLOWING = 459,
        FORTRAN = 460,
        FOUND = 461,
        G = 462,
        GENERAL = 463,
        GENERATED = 464,
        GO = 465,
        GOTO = 466,
        GRANTED = 467,
        HIERARCHY = 468,
        HISTOGRAM = 469, // Pyrrho 4.5
        IGNORE = 470,
        IMMEDIATE = 471,
        IMMEDIATELY = 472,
        IMPLEMENTATION = 473,
        INCLUDING = 474,
        INCREMENT = 475,
        INITIALLY = 476,
        INPUT = 477,
        INSTANCE = 478,
        INSTANTIABLE = 479,
        INSTEAD = 480,
        INVERTS = 481, // Pyrrho Metadata 5.7
        INVOKER = 482,
        IRI = 483, // Pyrrho 7
        ISOLATION = 484,
        JSON = 485, // Pyrrho 5.5
        K = 486,
        KEY = 487,
        KEY_MEMBER = 488,
        KEY_TYPE = 489,
        LAST = 490,
        LEGEND = 491, // Pyrrho Metadata 4.8
        LENGTH = 492,
        LEVEL = 493,
        LINE = 494, // Pyrrho 4.5
        LOCATOR = 495,
        M = 496,
        MAP = 497,
        MATCHED = 498,
        MAXVALUE = 499,
        MESSAGE_LENGTH = 500,
        MESSAGE_OCTET_LENGTH = 501,
        MESSAGE_TEXT = 502,
        MIME = 503, // Pyrrho 7
        MINVALUE = 504,
        MONOTONIC = 505, // Pyrrho 5.7
        MORE = 506,
        MUMPS = 507,
        NAME = 508,
        NAMES = 509,
        NESTING = 510,
        NEXT = 511,
        NFC = 512,
        NFD = 513,
        NFKC = 514,
        NFKD = 515,
        NORMALIZED = 516,
        NULLABLE = 517,
        NULLS = 518,
        NUMBER = 519,
        OCCURRENCE = 520,
        OCTETS = 521,
        OPTION = 522,
        OPTIONS = 523,
        ORDERING = 524,
        ORDINALITY = 525,
        OTHERS = 526,
        OUTPUT = 527,
        OVERRIDING = 528,
        OWNER = 529, // Pyrrho
        P = 530,
        PAD = 531,
        PARAMETER_MODE = 532,
        PARAMETER_NAME = 533,
        PARAMETER_ORDINAL_POSITION = 534,
        PARAMETER_SPECIFIC_CATALOG = 535,
        PARAMETER_SPECIFIC_NAME = 536,
        PARAMETER_SPECIFIC_SCHEMA = 537,
        PARTIAL = 538,
        PASCAL = 539,
        PATH = 540,
        PIE = 541, // Pyrrho 4.5
        PLACING = 542,
        PL1 = 543,
        POINTS = 544, // Pyrrho 4.5
        PRECEDING = 545,
        PRESERVE = 546,
        PRIOR = 547,
        PRIVILEGES = 548,
        PROFILING = 549, // Pyrrho
        PROVENANCE = 550, // Pyrrho
        PUBLIC = 551,
        READ = 552,
        REFERRED = 553, // 5.2
        REFERS = 554, // 5.2
        RELATIVE = 555,
        REPEATABLE = 556,
        RESPECT = 557,
        RESTART = 558,
        RESTRICT = 559,
        RETURNED_CARDINALITY = 560,
        RETURNED_LENGTH = 561,
        RETURNED_OCTET_LENGTH = 562,
        RETURNED_SQLSTATE = 563,
        ROLE = 564,
        ROUTINE = 565,
        ROUTINE_CATALOG = 566,
        ROUTINE_NAME = 567,
        ROUTINE_SCHEMA = 568,
        ROW_COUNT = 569,
        SCALE = 570,
        SCHEMA = 571,
        SCHEMA_NAME = 572,
        SCOPE_CATALOG = 573,
        SCOPE_NAME = 574,
        SCOPE_SCHEMA = 575,
        SECTION = 576,
        SECURITY = 577,
        SELF = 578,
        SEQUENCE = 579,
        SERIALIZABLE = 580,
        SERVER_NAME = 581,
        SESSION = 582,
        SETS = 583,
        SIMPLE = 584,
        SIZE = 585,
        SOURCE = 586,
        SPACE = 587,
        SPECIFIC_NAME = 588,
        SQLAGENT = 589, // Pyrrho 7
        STANDALONE = 590, // vol 14
        STATE = 591,
        STATEMENT = 592,
        STRUCTURE = 593,
        STYLE = 594,
        SUBCLASS_ORIGIN = 595,
        T = 596,
        TABLE_NAME = 597,
        TEMPORARY = 598,
        TIES = 599,
        TIMEOUT = 600, // Pyrrho
        TOP_LEVEL_COUNT = 601,
        TRANSACTION = 602,
        TRANSACTION_ACTIVE = 603,
        TRANSACTIONS_COMMITTED = 604,
        TRANSACTIONS_ROLLED_BACK = 605,
        TRANSFORM = 606,
        TRANSFORMS = 607,
        TRIGGER_CATALOG = 608,
        TRIGGER_NAME = 609,
        TRIGGER_SCHEMA = 610, // TYPE  is 267 but is not a reserved word
        TYPE_URI = 611, // Pyrrho
        UNBOUNDED = 612,
        UNCOMMITTED = 613,
        UNDER = 614,
        UNDO = 615,
        UNNAMED = 616,
        URL = 617,  // Pyrrho 7
        USAGE = 618,
        USER_DEFINED_TYPE_CATALOG = 619,
        USER_DEFINED_TYPE_CODE = 620,
        USER_DEFINED_TYPE_NAME = 621,
        USER_DEFINED_TYPE_SCHEMA = 622,
        VIEW = 623,
        WORK = 624,
        WRITE = 625,
        X = 626, // Pyrrho 4.5
        Y = 627, // Pyrrho 4.5
        ZONE = 628
    }
    /// <summary>
    /// These are the underlying (physical) datatypes used  for values in the database
    /// The file format is not machine specific: the engine uses long for Integer where possible, etc
    /// </summary>
    public enum DataType
    {
        Null,
        TimeStamp,  // Integer(UTC ticks)
        Interval,   // Integer[3] (years,months,ticks)
        Integer,    // 1024-bit Integer
        Numeric,    // 1024-bit Integer, precision, scale
        String,     // string: Integer length, length x byte
        Date,       // Integer (UTC ticks)
        TimeSpan,   // Integer (UTC ticks)
        Boolean,    // byte 3 values: T=1,F=0,U=255
        DomainRef,  // typedefpos, Integer els, els x data 
        Blob,       // Integer length, length x byte: Opaque binary type (Clob is String)
        Row,        // spec, Integer cols, cols x data
        Multiset,   // Integer els, els x data
        Array,		// Integer els, els x data
        Password   // A more secure type of string (write-only)
    }
    /// <summary>
    /// These are the supported character repertoires in SQL2011
    /// </summary>
	public enum CharSet
    {
        UCS, SQL_IDENTIFIER, SQL_CHARACTER, GRAPHIC_IRV, // GRAPHIC_IRV is also known as ASCII_GRAPHIC
        LATIN1, ISO8BIT, // ISO8BIT is also known as ASCII_FULL
        SQL_TEXT
    };
    /// <summary>
    /// An Exception class for reporting client errors
    /// </summary>
    internal class DBException : Exception // Client error 
    {
        internal string signal; // Compatible with SQL2011
        internal object[] objects; // additional data for insertion in (possibly localised) message format
        // diagnostic info (there is an active transaction unless we have just done a rollback)
        internal ATree<Sqlx, TypedValue> info = new BTree<Sqlx, TypedValue>(Sqlx.TRANSACTION_ACTIVE, new TInt(1));
        readonly TChar iso = new TChar("ISO 9075");
        readonly TChar pyrrho = new TChar("Pyrrho");
        /// <summary>
        /// Raise an exception to be localised and formatted by the client
        /// </summary>
        /// <param name="sqlstate">The signal</param>
        /// <param name="obs">objects to be included in the message</param>
        public DBException(string sqlstate, params object[] obs)
            : base(sqlstate)
        {
            signal = sqlstate;
            objects = obs;
            if (PyrrhoStart.TutorialMode)
            {
                Console.Write("Exception " + sqlstate);
                foreach (var o in obs)
                    Console.Write("|" + o.ToString());
                Console.WriteLine();
            }
        }
        /// <summary>
        /// Add diagnostic information to the exception
        /// </summary>
        /// <param name="k">diagnostic key as in SQL2011</param>
        /// <param name="v">value of this diagnostic</param>
        /// <returns>this (so we can chain diagnostics)</returns>
        internal DBException Add(Sqlx k, TypedValue v)
        {
            ATree<Sqlx, TypedValue>.Add(ref info, k, v ?? TNull.Value);
            return this;
        }
        internal DBException AddType(ObInfo t)
        {
            Add(Sqlx.TYPE, new TChar(t.ToString()));
            return this;
        }
        internal DBException AddType(Domain t)
        {
            Add(Sqlx.TYPE, new TChar(t.ToString()));
            return this;
        }
        internal DBException AddValue(TypedValue v)
        {
            Add(Sqlx.VALUE, v);
            return this;
        }
        internal DBException AddValue(Domain t)
        {
            Add(Sqlx.VALUE, new TChar(t.ToString()));
            return this;
        }
        /// <summary>
        /// Helper for SQL2011-defined exceptions
        /// </summary>
        /// <returns>this (so we can chain diagnostics)</returns>
        internal DBException ISO()
        {
            Add(Sqlx.CLASS_ORIGIN, iso);
            Add(Sqlx.SUBCLASS_ORIGIN, pyrrho);
            return this;
        }
        /// <summary>
        /// Helper for Pyrrho-defined exceptions
        /// </summary>
        /// <returns>this (so we can chain diagnostics)</returns>
        internal DBException Pyrrho()
        {
            Add(Sqlx.CLASS_ORIGIN, pyrrho);
            Add(Sqlx.SUBCLASS_ORIGIN, pyrrho);
            return this;
        }
        /// <summary>
        /// Helper for Pyrrho-defined exceptions in SQL-2011 class
        /// </summary>
        /// <returns>this (so we can chain diagnostics)</returns>
        internal DBException Mix()
        {
            Add(Sqlx.CLASS_ORIGIN, iso);
            Add(Sqlx.SUBCLASS_ORIGIN, pyrrho);
            return this;
        }
    }

    /// <summary>
    /// Supports the SQL2011 Interval data type. 
    /// Note that Intervals cannot have both year-month and day-second fields.
    /// </summary>
	internal class Interval
	{
		internal int years = 0, months = 0;
        internal long ticks = 0;
        internal bool yearmonth = true;
        public Interval(int y,int m) { years = y; months = m; }
        public Interval(long t) { ticks = t; yearmonth = false; }
        public override string ToString()
        {
            if (yearmonth)
                return "" + years + "Y" + months + "M";
            return "" + ticks;
        }
	}
    /// <summary>
    /// Row Version cookie (Sqlx.CHECK). See Laiho/Laux 2010.
    /// Row Versions are about durable data; but experimentally we extend this notion for incomplete transactions.
    /// Check allows transactions to find out if another transaction has overritten the row.
    /// Fields are modified only during commit serialisation
    /// </summary>
    internal class Rvv : BTree<long,(long,long?)>,IComparable
    {
        internal new static Rvv Empty = new Rvv();
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="d">the local database if any</param>
        /// <param name="t">the table defpos if local</param>
        /// <param name="r">remote database if any</param>
        /// <param name="df">the row defpos</param>
        Rvv() :base()
        { }
        protected Rvv(BTree<long,(long,long?)> t) : base(t.root) { }
        public static Rvv operator+(Rvv r,(long,Level4.Cursor) x)
        {
            var (rp, cu) = x;
            return new Rvv(r + (rp, (cu._defpos, cu._ppos)));
        }
        public static Rvv operator+(Rvv r,(long,long,long)x)
        {
            var (t, d, o) = x;
            return new Rvv(r + (t, (d, o)));
        }
        public static Rvv operator+(Rvv r,Rvv s)
        {
            if (r == Empty)
                return s;
            if (s == Empty)
                return r;
            var a = (BTree<long, (long, long?)>)r;
            var b = (BTree<long, (long, long?)>)s;
            return new Rvv(a + b); 
        }
        /// <summary>
        /// Validate an RVV string
        /// </summary>
        /// <param name="s">the string</param>
        /// <returns>the rvv</returns>
        internal bool Validate(Database db)
        {
            for (var b=First();b!=null;b=b.Next())
            {
                var t = (Table)db.objects[b.key()];
                var (d, o) = b.value();
                if (t.tableRows[d]?.time != o)
                    return false;
            }
            return true;
        }
        public static Rvv Parse(string s)
        {
            var r = Empty;
            var ss = s.Split(';');
            foreach(var t in ss)
            {
                var tt = t.Split(',');
                r += (long.Parse(tt[0]), long.Parse(tt[1]), long.Parse(tt[2]));
            }
            return r;
        }
        /// <summary>
        /// String version of an rvv
        /// </summary>
        /// <returns>the string version</returns>
        public override string ToString()
        {
            var sb = new StringBuilder();
            var sc = "";
            for (var b=First();b!=null;b=b.Next())
            {
                sb.Append(sc); sc = ";";
                sb.Append(b.key()); sb.Append(",");
                var (d, o) = b.value();
                sb.Append(d); sb.Append(","); sb.Append(o);
            }
            return sb.ToString();
        }
        /// <summary>
        /// IComparable implementation
        /// </summary>
        /// <param name="obj"></param>
        /// <returns></returns>
        public int CompareTo(object obj)
        {
            var that = obj as Rvv;
            if (that == null)
                return 1;
            int c = 0;
            var tb = that.First();
            var b = First();
            for (; c==0 && b != null && tb!=null; b = b.Next(),tb=tb.Next())
            {
                c = b.key().CompareTo(tb.key());
                if (c == 0)
                    c = b.value().Item1.CompareTo(tb.value().Item1);
                if (c == 0)
                    c = b.value().Item2.Value.CompareTo(tb.value().Item2.Value);
            }
            if (b != null)
                return 1;
            if (tb != null)
                return -1;
            return c;
        }

    }
    /// <summary>
    /// Supports the SQL2003 Date data type
    /// </summary>
	public class Date : IComparable
	{
		public DateTime date;
		internal Date(DateTime d)
		{
			date = d;
		}
        public override string ToString()
        {
            return date.ToString("dd/MM/yyyy");
        }
        #region IComparable Members

        public int CompareTo(object obj)
        {
            if (obj is Date dt)
                obj = dt.date;
            return date.CompareTo(obj);
        }

        #endregion
    }
}
