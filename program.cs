
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MetaQuotes.MT5CommonAPI;
using MetaQuotes.MT5ManagerAPI;

namespace MT5ManagerApiNet
{
    public class MT5Manager
    {
        public int OrderExecuteTimeout = 10000;

        public object ConnectLock = new object();

        public CIMTManagerAPI Manager;

        private RequestSink RequestSink;

        private OrderSink OrderSink;

        private ManagerSink ManagerSink;

        private DealerSink DealerSink;

        private TickSink TickSink;

        private SummarySink SummarySink;

        private PositionSink PositionSink;

        internal EventWaitHandle RequestEvent = new EventWaitHandle(initialState: false, EventResetMode.ManualReset);

        private List<string> Subscriptions = new List<string>();

        private Dictionary<string, Quote> Quotes = new Dictionary<string, Quote>();

        private Task ReconnectTask;

        private CancellationTokenSource ReconnectTaskCanceller;

        internal Logger Log;

        private Thread OnQuoteThread;

        private bool KillQuoteThread;

        private Thread DealerThread;

        private bool DealerThreadStop;

        public ProcessEvents ProcessEvents = ProcessEvents.ThreadPool;

        public int EventProcessTimeout = 30000;

        public ulong User { get; set; }

        public string Password { get; set; }

        public string Server { get; set; }

        public string Id { get; set; }

        public event OnQuoteEventHandler OnQuote;

        public event OnRequestAdd OnRequestAdd;

        public event OnRequestUpdate OnRequestUpdate;

        public event OnRequestDelete OnRequestDelete;

        public event OnDealerResult OnDealerResult;

        public event OnDealerAnswer OnDealerAnswer;

        public event OnTick OnTick;

        public event OnSummary OnSummary;

        public event OnOrderUpdate OnOrderUpdate;

        public event OnPositionUpdate OnPositionUpdate;

        public TradeRecord[] PositionHistory(ulong login, DateTime from = default(DateTime), DateTime to = default(DateTime))
        {
            if (to == default(DateTime))
            {
                to = DateTime.Now.AddDays(1.0);
            }

            Deal[] array = DealRequest(login, ConvertTo.Long(from), ConvertTo.Long(to));
            Dictionary<ulong, List<Deal>> dictionary = new Dictionary<ulong, List<Deal>>();
            Deal[] array2 = array;
            foreach (Deal deal in array2)
            {
                if (!dictionary.ContainsKey(deal.PositionID))
                {
                    dictionary.Add(deal.PositionID, new List<Deal>());
                }

                dictionary[deal.PositionID].Add(deal);
            }

            List<TradeRecord> list = new List<TradeRecord>();
            foreach (KeyValuePair<ulong, List<Deal>> item in dictionary)
            {
                TradeRecord tradeRecord = new TradeRecord();
                bool flag = false;
                bool flag2 = false;
                foreach (Deal item2 in item.Value)
                {
                    if (item2.Entry == 0 || item2.Entry == 2)
                    {
                        tradeRecord.OpenTime = ConvertTo.DateTimeMs(item2.TimeMsc);
                        tradeRecord.Login = item2.Login;
                        tradeRecord.OpenPrice = item2.Price;
                        tradeRecord.Volume = (int)(item2.Lots * 100.0);
                        tradeRecord.Reason = (DealReason)item2.Action;
                        if (item2.Entry == 2)
                        {
                            tradeRecord.Order = item2.Order;
                        }
                        else
                        {
                            tradeRecord.Order = item2.PositionID;
                        }

                        tradeRecord.Cmd = (OrderType)item2.Action;
                        tradeRecord.Magic = item2.ExpertID;
                        tradeRecord.Symbol = item2.Symbol;
                        tradeRecord.Commission = item2.Commission;
                        tradeRecord.Storage = item2.Storage;
                        tradeRecord.Comment = item2.Comment;
                        tradeRecord.Sl = item2.PriceSL;
                        tradeRecord.Tp = item2.PriceTP;
                        tradeRecord.Digits = item2.Digits;
                        flag = true;
                    }

                    if (item2.Entry == 1)
                    {
                        tradeRecord.Login = item2.Login;
                        tradeRecord.CloseTime = ConvertTo.DateTimeMs(item2.TimeMsc);
                        tradeRecord.ClosePrice = item2.Price;
                        tradeRecord.Commission += item2.Commission;
                        tradeRecord.Profit += item2.Profit;
                        tradeRecord.Storage += item2.Storage;
                        tradeRecord.Digits = item2.Digits;
                        tradeRecord.Sl = item2.PriceSL;
                        tradeRecord.Tp = item2.PriceTP;
                        flag2 = true;
                    }
                }

                if (flag && flag2)
                {
                    list.Add(tradeRecord);
                }
            }

            return list.ToArray();
        }

        public void Subscribe(string symbol)
        {
            lock (Subscriptions)
            {
                if (Subscriptions.Contains(symbol))
                {
                    return;
                }

                Subscriptions.Add(symbol);
                Manager.SelectedAdd(symbol);
                if (Subscriptions.Count != 1)
                {
                    return;
                }

                KillQuoteThread = false;
                OnQuoteThread = new Thread((ThreadStart)delegate
                {
                    while (!KillQuoteThread)
                    {
                        lock (Subscriptions)
                        {
                            foreach (string subscription in Subscriptions)
                            {
                                Manager.TickLast(subscription, out var tick);
                                if (Quotes.ContainsKey(subscription))
                                {
                                    Quote quote = Quotes[subscription];
                                    if (tick.bid != 0.0 && tick.ask != 0.0 && (quote.Bid != tick.bid || quote.Ask != tick.ask))
                                    {
                                        TickToQuote(tick, Quotes[subscription]);
                                        if (this.OnQuote != null)
                                        {
                                            this.OnQuote(this, Quotes[subscription]);
                                        }
                                    }
                                }
                                else if (tick.bid != 0.0 && tick.ask != 0.0)
                                {
                                    Quote quote2 = new Quote
                                    {
                                        Symbol = subscription
                                    };
                                    TickToQuote(tick, quote2);
                                    Quotes.Add(subscription, quote2);
                                    if (this.OnQuote != null)
                                    {
                                        this.OnQuote(this, quote2);
                                    }
                                }
                            }
                        }

                        Thread.Sleep(500);
                    }
                });
                OnQuoteThread.Start();
            }
        }

        private void TickToQuote(MetaQuotes.MT5CommonAPI.MTTickShort rec, Quote quote)
        {
            if (rec.datetime_msc != 0)
            {
                quote.Time = ConvertTo.DateTimeMs(rec.datetime_msc);
            }
            else
            {
                quote.Time = ConvertTo.DateTime(rec.datetime);
            }

            if (rec.bid != 0.0)
            {
                quote.Bid = rec.bid;
            }

            if (rec.ask != 0.0)
            {
                quote.Ask = rec.ask;
            }

            if (rec.last != 0.0)
            {
                quote.Last = rec.last;
            }

            if (rec.volume != 0)
            {
                quote.Volume = rec.volume;
            }
        }

        private void Connect(string server, ulong user, string password)
        {
            lock (ConnectLock)
            {
                Log = new Logger(this);
                Trial.Check();
                string empty = string.Empty;
                MTRetCode mTRetCode = MTRetCode.MT_RET_OK_NONE;
                if ((mTRetCode = SMTManagerAPIFactory.Initialize("")) != 0)
                {
                    throw new Exception($"Loading manager API failed ({mTRetCode})");
                }

                Manager = SMTManagerAPIFactory.CreateManager(SMTManagerAPIFactory.ManagerAPIVersion, out mTRetCode);
                if (mTRetCode != 0 || Manager == null)
                {
                    SMTManagerAPIFactory.Shutdown();
                    throw new Exception(string.Format("Creating manager interface failed ({0})", (mTRetCode == MTRetCode.MT_RET_OK) ? "Managed API is null" : mTRetCode.ToString()));
                }

                Server = server;
                User = user;
                Password = password;
                MTRetCode mTRetCode2 = Manager.Connect(server, user, password, null, CIMTManagerAPI.EnPumpModes.PUMP_MODE_FULL, 10000u);
                if (mTRetCode2 != 0)
                {
                    throw new Exception($"Connection failed ({mTRetCode2})");
                }

                RequestSink = new RequestSink(this);
                if (Manager.RequestSubscribe(RequestSink) != 0)
                {
                    throw new Exception("RequestSubscribe fail");
                }

                OrderSink = new OrderSink(this);
                if (Manager.OrderSubscribe(OrderSink) != 0)
                {
                    throw new Exception("OrderSubscribe fail");
                }

                ManagerSink = new ManagerSink(Manager);
                if (Manager.Subscribe(ManagerSink) != 0)
                {
                    throw new Exception("Subscribe(ManagerSink) fail");
                }

                DealerSink = new DealerSink(this);
                TickSink = new TickSink(this);
                if (Manager.TickSubscribe(TickSink) != 0)
                {
                    throw new Exception("TickSubscribe(TickSink) fail");
                }

                SummarySink = new SummarySink(this);
                if (Manager.SummarySubscribe(SummarySink) != 0)
                {
                    throw new Exception("SummarySubscribe(SummarySink) fail");
                }

                PositionSink = new PositionSink(this);
                if (Manager.PositionSubscribe(PositionSink) != 0)
                {
                    throw new Exception("PositionSubscribe(PositionSink) fail");
                }
            }
        }

        public void Login(string server, ulong user, string password)
        {
            Connect(server, user, password);
            if (ReconnectTask != null)
            {
                return;
            }

            ReconnectTaskCanceller = new CancellationTokenSource();
            ReconnectTask = Task.Run(delegate
            {
                while (!ReconnectTaskCanceller.IsCancellationRequested)
                {
                    try
                    {
                        try
                        {
                            TimeServerRequest(out var _);
                        }
                        catch (Exception)
                        {
                            Manager.Disconnect();
                            Connect(server, user, password);
                            Log.info($"Connected ({User},{Password},{Server})");
                        }
                    }
                    catch (Exception ex2)
                    {
                        Log.warn($"Reconnect exception({User},{Password},{Server})", ex2);
                    }

                    Thread.Sleep(3000);
                }
            }, ReconnectTaskCanceller.Token);
        }

        public Order[] OrderHsitory(ulong login, DateTime from, DateTime to)
        {
            return HistoryRequest(login, ConvertTo.Long(from), ConvertTo.Long(to));
        }

        public ulong Deposit(ulong login, double amount, CIMTDeal.EnDealAction type, string comment)
        {
            lock (Manager)
            {
                ulong deal_id;
                MTRetCode mTRetCode = Manager.DealerBalance(login, amount, (uint)type, comment, out deal_id);
                if (mTRetCode != MTRetCode.MT_RET_REQUEST_DONE)
                {
                    throw new Exception($"DealerBalance error ({mTRetCode})");
                }

                return deal_id;
            }
        }

        public Order OrderGetByTicket(ulong ticket)
        {
            CIMTOrderArray cIMTOrderArray = Manager.OrderCreateArray();
            Manager.OrderGetByTickets(new ulong[1] { ticket }, cIMTOrderArray);
            if (cIMTOrderArray.Total() == 0)
            {
                throw new Exception($"{ticket} not found");
            }

            return new Order(cIMTOrderArray.Next(0u), this);
        }

        public uint SendDealAsync(ulong login, CIMTOrder.EnOrderType type, string symbol, double lots, double price = 0.0)
        {
            CIMTRequest cIMTRequest = Manager.RequestCreate();
            cIMTRequest.Login(login);
            cIMTRequest.SourceLogin(User);
            cIMTRequest.Action(CIMTRequest.EnTradeActions.TA_DEALER_FIRST);
            cIMTRequest.Type(type);
            cIMTRequest.Volume(SMTMath.VolumeToInt(lots));
            cIMTRequest.Symbol(symbol);
            cIMTRequest.PriceOrder(price);
            DealerSend(cIMTRequest, DealerSink, out var id);
            return id;
        }

        public MTRequest SendDeal(ulong login, CIMTOrder.EnOrderType type, string symbol, double lots, double price = 0.0)
        {
            uint id = SendDealAsync(login, type, symbol, lots, price);
            return new OrderExecuteWaiter(this, id).Wait(OrderExecuteTimeout);
        }

        public uint CloseDealAsync(ulong ticket, double lots = 0.0, double price = 0.0)
        {
            CIMTPosition cIMTPosition = Manager.PositionCreate();
            Manager.PositionGetByTicket(ticket, cIMTPosition);
            CIMTRequest cIMTRequest = Manager.RequestCreate();
            cIMTRequest.Login(cIMTPosition.Login());
            cIMTRequest.SourceLogin(User);
            cIMTRequest.Action(CIMTRequest.EnTradeActions.TA_DEALER_FIRST);
            if (cIMTPosition.Action() == 0)
            {
                cIMTRequest.Type(CIMTOrder.EnOrderType.OP_SELL);
            }
            else
            {
                cIMTRequest.Type(CIMTOrder.EnOrderType.OP_BUY);
            }

            cIMTRequest.Volume(cIMTPosition.Volume());
            cIMTRequest.Symbol(cIMTPosition.Symbol());
            cIMTRequest.Position(ticket);
            cIMTRequest.PriceOrder(price);
            DealerSend(cIMTRequest, DealerSink, out var id);
            return id;
        }

        public MTRequest CloseDeal(ulong ticket, double lots = 0.0, double price = 0.0)
        {
            uint id = CloseDealAsync(ticket, lots, price);
            return new OrderExecuteWaiter(this, id).Wait(OrderExecuteTimeout);
        }

        public uint ModifyDealAsync(ulong ticket, double stoploss, double takeprofit = 0.0)
        {
            CIMTPosition cIMTPosition = Manager.PositionCreate();
            Manager.PositionGetByTicket(ticket, cIMTPosition);
            CIMTRequest cIMTRequest = Manager.RequestCreate();
            cIMTRequest.Login(cIMTPosition.Login());
            cIMTRequest.SourceLogin(User);
            cIMTRequest.Action(CIMTRequest.EnTradeActions.TA_DEALER_POS_MODIFY);
            cIMTRequest.Volume(cIMTPosition.Volume());
            cIMTRequest.Symbol(cIMTPosition.Symbol());
            cIMTRequest.Position(ticket);
            cIMTRequest.PriceSL(stoploss);
            cIMTRequest.PriceTP(takeprofit);
            DealerSend(cIMTRequest, DealerSink, out var id);
            return id;
        }

        public MTRequest ModifyDeal(ulong ticket, double stoploss, double takeprofit = 0.0)
        {
            uint id = ModifyDealAsync(ticket, stoploss, takeprofit);
            return new OrderExecuteWaiter(this, id).Wait(OrderExecuteTimeout);
        }

        public uint SendOrderAsync(ulong login, CIMTOrder.EnOrderType type, string symbol, double lots, double price)
        {
            CIMTRequest cIMTRequest = Manager.RequestCreate();
            cIMTRequest.Login(login);
            cIMTRequest.SourceLogin(User);
            cIMTRequest.Action(CIMTRequest.EnTradeActions.TA_DEALER_ORD_PENDING);
            cIMTRequest.Type(type);
            cIMTRequest.Volume(SMTMath.VolumeToInt(lots));
            cIMTRequest.Symbol(symbol);
            cIMTRequest.TypeTime(CIMTOrder.EnOrderTime.ORDER_TIME_GTC);
            cIMTRequest.TypeFill(CIMTOrder.EnOrderFilling.ORDER_FILL_RETURN);
            cIMTRequest.PriceOrder(price);
            DealerSend(cIMTRequest, DealerSink, out var id);
            return id;
        }

        public MTRequest SendOrder(ulong login, CIMTOrder.EnOrderType type, string symbol, double lots, double price)
        {
            uint id = SendOrderAsync(login, type, symbol, lots, price);
            return new OrderExecuteWaiter(this, id).Wait(OrderExecuteTimeout);
        }

        public uint CloseOrderAsync(ulong ticket)
        {
            Order order = OrderGetByTicket(ticket);
            CIMTRequest cIMTRequest = Manager.RequestCreate();
            cIMTRequest.Login(order.Login);
            cIMTRequest.SourceLogin(User);
            cIMTRequest.Action(CIMTRequest.EnTradeActions.TA_DEALER_ORD_REMOVE);
            cIMTRequest.Symbol(order.Symbol);
            cIMTRequest.Volume(order.VolumeCurrent);
            cIMTRequest.Order(ticket);
            cIMTRequest.Type((CIMTOrder.EnOrderType)order.Type);
            DealerSend(cIMTRequest, DealerSink, out var id);
            return id;
        }

        public MTRequest CloseOrder(ulong ticket)
        {
            uint id = CloseOrderAsync(ticket);
            return new OrderExecuteWaiter(this, id).Wait(OrderExecuteTimeout);
        }

        public uint ModifyOrderAsync(ulong ticket, double price, double stoploss = 0.0, double takeprofit = 0.0)
        {
            Order order = OrderGetByTicket(ticket);
            CIMTRequest cIMTRequest = Manager.RequestCreate();
            cIMTRequest.Login(order.Login);
            cIMTRequest.SourceLogin(User);
            cIMTRequest.Action(CIMTRequest.EnTradeActions.TA_DEALER_ORD_MODIFY);
            cIMTRequest.Volume(order.VolumeCurrent);
            cIMTRequest.Symbol(order.Symbol);
            cIMTRequest.Order(ticket);
            cIMTRequest.PriceOrder(price);
            cIMTRequest.PriceSL(stoploss);
            cIMTRequest.PriceTP(takeprofit);
            cIMTRequest.Type((CIMTOrder.EnOrderType)order.Type);
            cIMTRequest.TypeTime(CIMTOrder.EnOrderTime.ORDER_TIME_GTC);
            cIMTRequest.TypeFill(CIMTOrder.EnOrderFilling.ORDER_FILL_RETURN);
            DealerSend(cIMTRequest, DealerSink, out var id);
            return id;
        }

        public MTRequest ModifyOrder(ulong ticket, double price, double stoploss = 0.0, double takeprofit = 0.0)
        {
            uint id = ModifyOrderAsync(ticket, price, stoploss, takeprofit);
            return new OrderExecuteWaiter(this, id).Wait(OrderExecuteTimeout);
        }

        public User GetUserInfo(ulong login)
        {
            lock (Manager)
            {
                CIMTUser cIMTUser = Manager.UserCreate();
                if (cIMTUser == null)
                {
                    throw new Exception("UserCreate fail");
                }

                MTRetCode mTRetCode = Manager.UserRequest(login, cIMTUser);
                if (mTRetCode != 0)
                {
                    throw new Exception($"UserRequest error ({mTRetCode})");
                }

                return new User(cIMTUser, this);
            }
        }

        public void DealerAnswer(CIMTConfirm confirm)
        {
            MTRetCode mTRetCode = Manager.DealerAnswer(confirm);
            if (mTRetCode != 0)
            {
                throw new Exception($"DealerAnswer error({mTRetCode})");
            }
        }

        public Account GetAccountInfo(ulong login)
        {
            lock (Manager)
            {
                CIMTAccount cIMTAccount = Manager.UserCreateAccount();
                if (cIMTAccount == null)
                {
                    throw new Exception("UserCreateAccount fail");
                }

                MTRetCode mTRetCode = Manager.UserAccountRequest(login, cIMTAccount);
                if (mTRetCode != 0)
                {
                    throw new Exception($"UserAccountRequest error({mTRetCode})");
                }

                return new Account(cIMTAccount, this);
            }
        }

        public Deal[] GetUserDeals(ulong login, DateTime time_from, DateTime time_to)
        {
            CIMTDealArray cIMTDealArray = Manager.DealCreateArray();
            if (cIMTDealArray == null)
            {
                Manager.LoggerOut(EnMTLogCode.MTLogErr, "DealCreateArray fail");
                throw new Exception("DealCreateArray fail");
            }

            MTRetCode mTRetCode = Manager.DealRequest(login, SMTTime.FromDateTime(time_from), SMTTime.FromDateTime(time_to), cIMTDealArray);
            if (mTRetCode != 0)
            {
                throw new Exception($"DealRequest fail({mTRetCode})");
            }

            return ConvertArray(cIMTDealArray);
        }

        private Deal[] ConvertArray(CIMTDealArray array)
        {
            throw new NotImplementedException();
        }

        public void DealerStart()
        {
            if (DealerThread != null && DealerThread.IsAlive)
            {
                throw new Exception("Dealer thread already running");
            }

            DealerThreadStop = false;
            MTRetCode mTRetCode = Manager.DealerStart();
            if (mTRetCode != 0)
            {
                throw new Exception($"DealerStart fail({mTRetCode})");
            }

            DealerThread = new Thread(new ThreadStart(DealerThreadProc));
            DealerThread.Start();
        }

        public void DealerThreadProc()
        {
            while (!DealerThreadStop)
            {
                if (RequestEvent.WaitOne(500))
                {
                    CIMTRequest cIMTRequest = Manager.RequestCreate();
                    while (Manager.DealerGet(cIMTRequest) == MTRetCode.MT_RET_OK)
                    {
                        OnRequestAddProc(cIMTRequest);
                    }
                }
            }
        }

        public void DocumentDelete(ulong document_id)
        {
            MTRetCode mTRetCode = Manager.DocumentDelete(document_id);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public MTRetCode[] DocumentDeleteBatch(ulong[] document_ids)
        {
            MTRetCode[] array = new MTRetCode[document_ids.Length];
            MTRetCode mTRetCode = Manager.DocumentDeleteBatch(document_ids, array);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return array;
        }

        public Document DocumentRequest(ulong document_id)
        {
            CIMTDocument cIMTDocument = Manager.DocumentCreate();
            MTRetCode mTRetCode = Manager.DocumentRequest(document_id, cIMTDocument);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new Document(cIMTDocument, this);
        }

        public Document[] DocumentRequestByClient(ulong client_id)
        {
            CIMTDocumentArray cIMTDocumentArray = Manager.DocumentCreateArray();
            MTRetCode mTRetCode = Manager.DocumentRequestByClient(client_id, cIMTDocumentArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Document[])ConvertToNetArray(cIMTDocumentArray);
        }

        public Document[] DocumentRequestHistory(ulong document_id, ulong author, long from, long to)
        {
            CIMTDocumentArray cIMTDocumentArray = Manager.DocumentCreateArray();
            MTRetCode mTRetCode = Manager.DocumentRequestHistory(document_id, author, from, to, cIMTDocumentArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Document[])ConvertToNetArray(cIMTDocumentArray);
        }

        public void CommentAdd(Comment comment)
        {
            MTRetCode mTRetCode = Manager.CommentAdd(comment.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public MTRetCode[] CommentAddBatch(Comment[] comments)
        {
            MTRetCode[] array = new MTRetCode[comments.Length];
            MTRetCode mTRetCode = Manager.CommentAddBatch((CIMTCommentArray)ConvertFromNetArray(comments, "Comment[]", "CIMTCommentArray"), array);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return array;
        }

        public void CommentUpdate(Comment comment)
        {
            MTRetCode mTRetCode = Manager.CommentUpdate(comment.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public MTRetCode[] CommentUpdateBatch(Comment[] comments)
        {
            MTRetCode[] array = new MTRetCode[comments.Length];
            MTRetCode mTRetCode = Manager.CommentUpdateBatch((CIMTCommentArray)ConvertFromNetArray(comments, "Comment[]", "CIMTCommentArray"), array);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return array;
        }

        public void CommentDelete(ulong comment_id)
        {
            MTRetCode mTRetCode = Manager.CommentDelete(comment_id);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public MTRetCode[] CommentDeleteBatch(ulong[] comment_ids)
        {
            MTRetCode[] array = new MTRetCode[comment_ids.Length];
            MTRetCode mTRetCode = Manager.CommentDeleteBatch(comment_ids, array);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return array;
        }

        public Comment CommentRequest(ulong comment_id)
        {
            CIMTComment cIMTComment = Manager.CommentCreate();
            MTRetCode mTRetCode = Manager.CommentRequest(comment_id, cIMTComment);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new Comment(cIMTComment, this);
        }

        public Comment[] CommentRequestByClient(ulong client_id, uint position, uint total)
        {
            CIMTCommentArray cIMTCommentArray = Manager.CommentCreateArray();
            MTRetCode mTRetCode = Manager.CommentRequestByClient(client_id, position, total, cIMTCommentArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Comment[])ConvertToNetArray(cIMTCommentArray);
        }

        public Comment[] CommentRequestByDocument(ulong document_id, uint position, uint total)
        {
            CIMTCommentArray cIMTCommentArray = Manager.CommentCreateArray();
            MTRetCode mTRetCode = Manager.CommentRequestByDocument(document_id, position, total, cIMTCommentArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Comment[])ConvertToNetArray(cIMTCommentArray);
        }

        public void AttachmentAdd(Attachment attach)
        {
            MTRetCode mTRetCode = Manager.AttachmentAdd(attach.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public MTRetCode[] AttachmentAddBatch(Attachment[] attachments)
        {
            MTRetCode[] array = new MTRetCode[attachments.Length];
            MTRetCode mTRetCode = Manager.AttachmentAddBatch((CIMTAttachmentArray)ConvertFromNetArray(attachments, "Attachment[]", "CIMTAttachmentArray"), array);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return array;
        }

        public Attachment[] AttachmentRequest(ulong[] attachment_ids)
        {
            CIMTAttachmentArray cIMTAttachmentArray = Manager.AttachmentCreateArray();
            MTRetCode mTRetCode = Manager.AttachmentRequest(attachment_ids, cIMTAttachmentArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Attachment[])ConvertToNetArray(cIMTAttachmentArray);
        }

        public void UserAccountSubscribe(CIMTAccountSink sink)
        {
            MTRetCode mTRetCode = Manager.UserAccountSubscribe(sink);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void UserAccountUnsubscribe(CIMTAccountSink sink)
        {
            MTRetCode mTRetCode = Manager.UserAccountUnsubscribe(sink);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public User[] UserRequestByLogins(ulong[] logins)
        {
            CIMTUserArray cIMTUserArray = Manager.UserCreateArray();
            MTRetCode mTRetCode = Manager.UserRequestByLogins(logins, cIMTUserArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (User[])ConvertToNetArray(cIMTUserArray);
        }

        public void SubscriptionCfgSubscribe(CIMTConSubscriptionSink sink)
        {
            MTRetCode mTRetCode = Manager.SubscriptionCfgSubscribe(sink);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void SubscriptionCfgUnsubscribe(CIMTConSubscriptionSink sink)
        {
            MTRetCode mTRetCode = Manager.SubscriptionCfgUnsubscribe(sink);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public uint SubscriptionCfgTotal()
        {
            return Manager.SubscriptionCfgTotal();
        }

        public void SubscriptionCfgNext(uint pos, ConSubscription config)
        {
            CIMTConSubscription cIMTConSubscription = Manager.SubscriptionCfgCreate();
            MTRetCode mTRetCode = Manager.SubscriptionCfgNext(pos, config.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public ConSubscription SubscriptionCfgGet(string name)
        {
            CIMTConSubscription cIMTConSubscription = Manager.SubscriptionCfgCreate();
            MTRetCode mTRetCode = Manager.SubscriptionCfgGet(name, cIMTConSubscription);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new ConSubscription(cIMTConSubscription, this);
        }

        public ConSubscription SubscriptionCfgGetByID(ulong id)
        {
            CIMTConSubscription cIMTConSubscription = Manager.SubscriptionCfgCreate();
            MTRetCode mTRetCode = Manager.SubscriptionCfgGetByID(id, cIMTConSubscription);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new ConSubscription(cIMTConSubscription, this);
        }

        public ConSubscription SubscriptionCfgRequest(string name)
        {
            CIMTConSubscription cIMTConSubscription = Manager.SubscriptionCfgCreate();
            MTRetCode mTRetCode = Manager.SubscriptionCfgRequest(name, cIMTConSubscription);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new ConSubscription(cIMTConSubscription, this);
        }

        public ConSubscription SubscriptionCfgRequestByID(ulong id)
        {
            CIMTConSubscription cIMTConSubscription = Manager.SubscriptionCfgCreate();
            MTRetCode mTRetCode = Manager.SubscriptionCfgRequestByID(id, cIMTConSubscription);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new ConSubscription(cIMTConSubscription, this);
        }

        public void SubscriptionJoin(ulong login, ulong subscription, Subscription record, SubscriptionHistory history)
        {
            MTRetCode mTRetCode = Manager.SubscriptionJoin(login, subscription, record.GetRoot(Manager), history.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void SubscriptionJoinBatch(ulong[] logins, ulong[] subscriptions, MTRetCode[] results, Subscription[] records, SubscriptionHistory[] history)
        {
            MTRetCode mTRetCode = Manager.SubscriptionJoinBatch(logins, subscriptions, results, (CIMTSubscriptionArray)ConvertFromNetArray(records, "Subscription[]", "CIMTSubscriptionArray"), (CIMTSubscriptionHistoryArray)ConvertFromNetArray(history, "SubscriptionHistory[]", "CIMTSubscriptionHistoryArray"));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void SubscriptionCancel(ulong login, ulong subscription, Subscription record, SubscriptionHistory history)
        {
            MTRetCode mTRetCode = Manager.SubscriptionCancel(login, subscription, record.GetRoot(Manager), history.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void SubscriptionCancelBatch(ulong[] logins, ulong[] subscriptions, MTRetCode[] results, Subscription[] records, SubscriptionHistory[] history)
        {
            MTRetCode mTRetCode = Manager.SubscriptionCancelBatch(logins, subscriptions, results, (CIMTSubscriptionArray)ConvertFromNetArray(records, "Subscription[]", "CIMTSubscriptionArray"), (CIMTSubscriptionHistoryArray)ConvertFromNetArray(history, "SubscriptionHistory[]", "CIMTSubscriptionHistoryArray"));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void SubscriptionUpdate(Subscription subscription)
        {
            MTRetCode mTRetCode = Manager.SubscriptionUpdate(subscription.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public MTRetCode[] SubscriptionUpdateBatch(Subscription[] records)
        {
            MTRetCode[] array = new MTRetCode[records.Length];
            MTRetCode mTRetCode = Manager.SubscriptionUpdateBatch((CIMTSubscriptionArray)ConvertFromNetArray(records, "Subscription[]", "CIMTSubscriptionArray"), array);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return array;
        }

        public void SubscriptionDelete(ulong id)
        {
            MTRetCode mTRetCode = Manager.SubscriptionDelete(id);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void SubscriptionDeleteBatch(ulong[] ids, MTRetCode[] results)
        {
            MTRetCode mTRetCode = Manager.SubscriptionDeleteBatch(ids, results);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public Subscription[] SubscriptionRequest(ulong login)
        {
            CIMTSubscriptionArray cIMTSubscriptionArray = Manager.SubscriptionCreateArray();
            MTRetCode mTRetCode = Manager.SubscriptionRequest(login, cIMTSubscriptionArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Subscription[])ConvertToNetArray(cIMTSubscriptionArray);
        }

        public Subscription SubscriptionRequestByID(ulong id)
        {
            CIMTSubscription cIMTSubscription = Manager.SubscriptionCreate();
            MTRetCode mTRetCode = Manager.SubscriptionRequestByID(id, cIMTSubscription);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new Subscription(cIMTSubscription, this);
        }

        public Subscription[] SubscriptionRequestByGroup(string group)
        {
            CIMTSubscriptionArray cIMTSubscriptionArray = Manager.SubscriptionCreateArray();
            MTRetCode mTRetCode = Manager.SubscriptionRequestByGroup(group, cIMTSubscriptionArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Subscription[])ConvertToNetArray(cIMTSubscriptionArray);
        }

        public Subscription[] SubscriptionRequestByLogins(ulong[] logins)
        {
            CIMTSubscriptionArray cIMTSubscriptionArray = Manager.SubscriptionCreateArray();
            MTRetCode mTRetCode = Manager.SubscriptionRequestByLogins(logins, cIMTSubscriptionArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Subscription[])ConvertToNetArray(cIMTSubscriptionArray);
        }

        public Subscription[] SubscriptionRequestByIDs(ulong[] ids)
        {
            CIMTSubscriptionArray cIMTSubscriptionArray = Manager.SubscriptionCreateArray();
            MTRetCode mTRetCode = Manager.SubscriptionRequestByIDs(ids, cIMTSubscriptionArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Subscription[])ConvertToNetArray(cIMTSubscriptionArray);
        }

        public void SubscriptionHistoryUpdate(SubscriptionHistory subscription)
        {
            MTRetCode mTRetCode = Manager.SubscriptionHistoryUpdate(subscription.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public MTRetCode[] SubscriptionHistoryUpdateBatch(SubscriptionHistory[] records)
        {
            MTRetCode[] array = new MTRetCode[records.Length];
            MTRetCode mTRetCode = Manager.SubscriptionHistoryUpdateBatch((CIMTSubscriptionHistoryArray)ConvertFromNetArray(records, "SubscriptionHistory[]", "CIMTSubscriptionHistoryArray"), array);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return array;
        }

        public void SubscriptionHistoryDelete(ulong id)
        {
            MTRetCode mTRetCode = Manager.SubscriptionHistoryDelete(id);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void SubscriptionHistoryDeleteBatch(ulong[] ids, MTRetCode[] results)
        {
            MTRetCode mTRetCode = Manager.SubscriptionHistoryDeleteBatch(ids, results);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public SubscriptionHistory[] SubscriptionHistoryRequest(ulong login, long from, long to)
        {
            CIMTSubscriptionHistoryArray cIMTSubscriptionHistoryArray = Manager.SubscriptionHistoryCreateArray();
            MTRetCode mTRetCode = Manager.SubscriptionHistoryRequest(login, from, to, cIMTSubscriptionHistoryArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (SubscriptionHistory[])ConvertToNetArray(cIMTSubscriptionHistoryArray);
        }

        public SubscriptionHistory SubscriptionHistoryRequestByID(ulong id)
        {
            CIMTSubscriptionHistory cIMTSubscriptionHistory = Manager.SubscriptionHistoryCreate();
            MTRetCode mTRetCode = Manager.SubscriptionHistoryRequestByID(id, cIMTSubscriptionHistory);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new SubscriptionHistory(cIMTSubscriptionHistory, this);
        }

        public SubscriptionHistory[] SubscriptionHistoryRequestByGroup(string group, long from, long to)
        {
            CIMTSubscriptionHistoryArray cIMTSubscriptionHistoryArray = Manager.SubscriptionHistoryCreateArray();
            MTRetCode mTRetCode = Manager.SubscriptionHistoryRequestByGroup(group, from, to, cIMTSubscriptionHistoryArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (SubscriptionHistory[])ConvertToNetArray(cIMTSubscriptionHistoryArray);
        }

        public SubscriptionHistory[] SubscriptionHistoryRequestByLogins(ulong[] logins, long from, long to)
        {
            CIMTSubscriptionHistoryArray cIMTSubscriptionHistoryArray = Manager.SubscriptionHistoryCreateArray();
            MTRetCode mTRetCode = Manager.SubscriptionHistoryRequestByLogins(logins, from, to, cIMTSubscriptionHistoryArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (SubscriptionHistory[])ConvertToNetArray(cIMTSubscriptionHistoryArray);
        }

        public SubscriptionHistory[] SubscriptionHistoryRequestByIDs(ulong[] ids)
        {
            CIMTSubscriptionHistoryArray cIMTSubscriptionHistoryArray = Manager.SubscriptionHistoryCreateArray();
            MTRetCode mTRetCode = Manager.SubscriptionHistoryRequestByIDs(ids, cIMTSubscriptionHistoryArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (SubscriptionHistory[])ConvertToNetArray(cIMTSubscriptionHistoryArray);
        }

        public void Dispose()
        {
            Manager.Dispose();
        }

        public void SpreadSubscribe(CIMTConSpreadSink sink)
        {
            MTRetCode mTRetCode = Manager.SpreadSubscribe(sink);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void SpreadUnsubscribe(CIMTConSpreadSink sink)
        {
            MTRetCode mTRetCode = Manager.SpreadUnsubscribe(sink);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public uint SpreadTotal()
        {
            return Manager.SpreadTotal();
        }

        public ConSpread SpreadNext(uint pos)
        {
            CIMTConSpread cIMTConSpread = Manager.SpreadCreate();
            MTRetCode mTRetCode = Manager.SpreadNext(pos, cIMTConSpread);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new ConSpread(cIMTConSpread, this);
        }

        public void TradeProfit(string group, string symbol, CIMTOrder.EnOrderType type, ulong volume, double price_open, double price_close, out double profit, out double profit_rate)
        {
            MTRetCode mTRetCode = Manager.TradeProfit(group, symbol, type, volume, price_open, price_close, out profit, out profit_rate);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void TradeRateBuy(string basec, string currency, out double rate, string group, string symbol, double price)
        {
            MTRetCode mTRetCode = Manager.TradeRateBuy(basec, currency, out rate, group, symbol, price);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void TradeRateSell(string basec, string currency, out double rate, string group, string symbol, double price)
        {
            MTRetCode mTRetCode = Manager.TradeRateSell(basec, currency, out rate, group, symbol, price);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void TradeMarginCheck(Order order, Account account_new, Account account_current)
        {
            MTRetCode mTRetCode = Manager.TradeMarginCheck(order.GetRoot(Manager), account_new.GetRoot(Manager), account_current.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void TradeMarginCheck(ulong login, string symbol, CIMTOrder.EnOrderType type, ulong volume, double price, Account account_new, Account account_current)
        {
            MTRetCode mTRetCode = Manager.TradeMarginCheck(login, symbol, type, volume, price, account_new.GetRoot(Manager), account_current.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void TradeProfitExt(string group, string symbol, CIMTOrder.EnOrderType type, ulong volume, double price_open, double price_close, out double profit, out double profit_rate)
        {
            MTRetCode mTRetCode = Manager.TradeProfitExt(group, symbol, type, volume, price_open, price_close, out profit, out profit_rate);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void TradeMarginCheckExt(ulong login, string symbol, CIMTOrder.EnOrderType type, ulong volume, double price, Account account_new, Account account_current)
        {
            MTRetCode mTRetCode = Manager.TradeMarginCheckExt(login, symbol, type, volume, price, account_new.GetRoot(Manager), account_current.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void NetworkRescan(uint flags, uint timeout)
        {
            MTRetCode mTRetCode = Manager.NetworkRescan(flags, timeout);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public ulong NetworkBytesSent()
        {
            return Manager.NetworkBytesSent();
        }

        public ulong NetworkBytesRead()
        {
            return Manager.NetworkBytesRead();
        }

        public void NetworkServer(out string server)
        {
            MTRetCode mTRetCode = Manager.NetworkServer(out server);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void NetworkAddress(out string address)
        {
            MTRetCode mTRetCode = Manager.NetworkAddress(out address);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public uint OnlineTotal()
        {
            return Manager.OnlineTotal();
        }

        public Online OnlineNext(uint pos)
        {
            CIMTOnline cIMTOnline = Manager.OnlineCreate();
            MTRetCode mTRetCode = Manager.OnlineNext(pos, cIMTOnline);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new Online(cIMTOnline, this);
        }

        public Online[] OnlineGet(ulong login)
        {
            CIMTOnlineArray cIMTOnlineArray = Manager.OnlineCreateArray();
            MTRetCode mTRetCode = Manager.OnlineGet(login, cIMTOnlineArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Online[])ConvertToNetArray(cIMTOnlineArray);
        }

        public void OnlineDisconnect(Online online)
        {
            MTRetCode mTRetCode = Manager.OnlineDisconnect(online.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public MTRetCode[] OnlineDisconnectBatch(Online[] online)
        {
            MTRetCode[] array = new MTRetCode[online.Length];
            MTRetCode mTRetCode = Manager.OnlineDisconnectBatch((CIMTOnlineArray)ConvertFromNetArray(online, "Online[]", "CIMTOnlineArray"), array);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return array;
        }

        public void TradeAccountSet(long request_id, User user, Account account, Order[] orders, Position[] positions)
        {
            MTRetCode mTRetCode = Manager.TradeAccountSet(request_id, user.GetRoot(Manager), account.GetRoot(Manager), (CIMTOrderArray)ConvertFromNetArray(orders, "Order[]", "CIMTOrderArray"), (CIMTPositionArray)ConvertFromNetArray(positions, "Position[]", "CIMTPositionArray"));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void NotificationsSend(ulong[] logins, string message)
        {
            MTRetCode mTRetCode = Manager.NotificationsSend(logins, message);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void NotificationsSend(string metaquotes_ids, string message)
        {
            MTRetCode mTRetCode = Manager.NotificationsSend(metaquotes_ids, message);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public byte[] SettingGet(string section, string key)
        {
            byte[] outdata;
            MTRetCode mTRetCode = Manager.SettingGet(section, key, out outdata);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return outdata;
        }

        public void SettingSet(string section, string key, byte[] indata)
        {
            MTRetCode mTRetCode = Manager.SettingSet(section, key, indata);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void SettingDelete(string section, string key)
        {
            MTRetCode mTRetCode = Manager.SettingDelete(section, key);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public MetaQuotes.MT5CommonAPI.MTChartBar[] ChartRequest(string symbol, long from, long to)
        {
            MTRetCode res;
            MetaQuotes.MT5CommonAPI.MTChartBar[] result = Manager.ChartRequest(symbol, from, to, out res);
            if (res != 0)
            {
                throw new ApiException(res);
            }

            return result;
        }

        public void ChartDelete(string symbol, long[] bars_dates)
        {
            MTRetCode mTRetCode = Manager.ChartDelete(symbol, bars_dates);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void ChartUpdate(string symbol, MetaQuotes.MT5CommonAPI.MTChartBar[] bars)
        {
            MTRetCode mTRetCode = Manager.ChartUpdate(symbol, bars);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void ChartReplace(string symbol, long from, long to, MetaQuotes.MT5CommonAPI.MTChartBar[] bars)
        {
            MTRetCode mTRetCode = Manager.ChartReplace(symbol, from, to, bars);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void ChartSplit(string symbol, uint new_shares, uint old_shares, uint rounding_rule, long datetime_from, long datetime_to)
        {
            MTRetCode mTRetCode = Manager.ChartSplit(symbol, new_shares, old_shares, rounding_rule, datetime_from, datetime_to);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public MetaQuotes.MT5CommonAPI.MTTickShort[] TickHistoryRequestRaw(string symbol, long from, long to)
        {
            MTRetCode res;
            MetaQuotes.MT5CommonAPI.MTTickShort[] result = Manager.TickHistoryRequestRaw(symbol, from, to, out res);
            if (res != 0)
            {
                throw new ApiException(res);
            }

            return result;
        }

        public void TickHistoryAdd(string symbol, MetaQuotes.MT5CommonAPI.MTTickShort[] ticks)
        {
            MTRetCode mTRetCode = Manager.TickHistoryAdd(symbol, ticks);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void TickHistoryReplace(string symbol, long from_msc, long to_msc, MetaQuotes.MT5CommonAPI.MTTickShort[] ticks)
        {
            MTRetCode mTRetCode = Manager.TickHistoryReplace(symbol, from_msc, to_msc, ticks);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void EmailSend(string account, string to, string to_name, string subject, string body)
        {
            MTRetCode mTRetCode = Manager.EmailSend(account, to, to_name, subject, body);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void MessengerSend(string destination, string group, string sender, string text)
        {
            MTRetCode mTRetCode = Manager.MessengerSend(destination, group, sender, text);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void MessengerVerifyPhone(string phone_number)
        {
            MTRetCode mTRetCode = Manager.MessengerVerifyPhone(phone_number);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void ClientAdd(Client client)
        {
            MTRetCode mTRetCode = Manager.ClientAdd(client.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public MTRetCode[] ClientAddBatch(Client[] clients)
        {
            MTRetCode[] array = new MTRetCode[clients.Length];
            MTRetCode mTRetCode = Manager.ClientAddBatch((CIMTClientArray)ConvertFromNetArray(clients, "Cient[]", "CIMTClientArray"), array);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return array;
        }

        public void ClientUpdate(Client client)
        {
            MTRetCode mTRetCode = Manager.ClientUpdate(client.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public MTRetCode[] ClientUpdateBatch(Client[] clients)
        {
            MTRetCode[] array = new MTRetCode[clients.Length];
            MTRetCode mTRetCode = Manager.ClientUpdateBatch((CIMTClientArray)ConvertFromNetArray(clients, "Cient[]", "CIMTClientArray"), array);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return array;
        }

        public void ClientDelete(ulong client_id)
        {
            MTRetCode mTRetCode = Manager.ClientDelete(client_id);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public MTRetCode[] ClientDeleteBatch(ulong[] client_ids)
        {
            MTRetCode[] array = new MTRetCode[client_ids.Length];
            MTRetCode mTRetCode = Manager.ClientDeleteBatch(client_ids, array);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return array;
        }

        public Client ClientRequest(ulong client_id)
        {
            CIMTClient cIMTClient = Manager.ClientCreate();
            MTRetCode mTRetCode = Manager.ClientRequest(client_id, cIMTClient);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new Client(cIMTClient, this);
        }

        public Client[] ClientRequestByGroup(string groups)
        {
            CIMTClientArray cIMTClientArray = Manager.ClientCreateArray();
            MTRetCode mTRetCode = Manager.ClientRequestByGroup(groups, cIMTClientArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Client[])ConvertToNetArray(cIMTClientArray);
        }

        public Client[] ClientRequestHistory(ulong client_id, ulong author, long from, long to)
        {
            CIMTClientArray cIMTClientArray = Manager.ClientCreateArray();
            MTRetCode mTRetCode = Manager.ClientRequestHistory(client_id, author, from, to, cIMTClientArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Client[])ConvertToNetArray(cIMTClientArray);
        }

        public void ClientUserAdd(ulong client_id, ulong login)
        {
            MTRetCode mTRetCode = Manager.ClientUserAdd(client_id, login);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public MTRetCode[] ClientUserAddBatch(ulong client_id, ulong[] logins)
        {
            MTRetCode[] array = new MTRetCode[logins.Length];
            MTRetCode mTRetCode = Manager.ClientUserAddBatch(client_id, logins, array);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return array;
        }

        public void ClientUserDelete(ulong client_id, ulong login)
        {
            MTRetCode mTRetCode = Manager.ClientUserDelete(client_id, login);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public MTRetCode[] ClientUserDeleteBatch(ulong client_id, ulong[] logins)
        {
            MTRetCode[] array = new MTRetCode[logins.Length];
            MTRetCode mTRetCode = Manager.ClientUserDeleteBatch(client_id, logins, array);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return array;
        }

        public ulong[] ClientUserRequest(ulong client_id)
        {
            ulong[] logins;
            MTRetCode mTRetCode = Manager.ClientUserRequest(client_id, out logins);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return logins;
        }

        public void DocumentAdd(Document document)
        {
            MTRetCode mTRetCode = Manager.DocumentAdd(document.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public MTRetCode[] DocumentAddBatch(Document[] documents)
        {
            MTRetCode[] array = new MTRetCode[documents.Length];
            MTRetCode mTRetCode = Manager.DocumentAddBatch((CIMTDocumentArray)ConvertFromNetArray(documents, "Document[]", "CIMTDocumentArray"), array);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return array;
        }

        public void DocumentUpdate(Document document)
        {
            MTRetCode mTRetCode = Manager.DocumentUpdate(document.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public MTRetCode[] DocumentUpdateBatchArray(Document[] documents)
        {
            MTRetCode[] array = new MTRetCode[documents.Length];
            MTRetCode mTRetCode = Manager.DocumentUpdateBatch((CIMTDocumentArray)ConvertFromNetArray(documents, "Document[]", "CIMTDocumentArray"), array);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return array;
        }

        public void BookSubscribeBatch(string[] symbols, CIMTBookSink sink)
        {
            MTRetCode mTRetCode = Manager.BookSubscribeBatch(symbols, sink);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void BookUnsubscribeBatch(string[] symbols, CIMTBookSink sink)
        {
            MTRetCode mTRetCode = Manager.BookUnsubscribeBatch(symbols, sink);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void MailSubscribe(CIMTMailSink sink)
        {
            MTRetCode mTRetCode = Manager.MailSubscribe(sink);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void MailUnsubscribe(CIMTMailSink sink)
        {
            MTRetCode mTRetCode = Manager.MailUnsubscribe(sink);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public uint MailTotal()
        {
            return Manager.MailTotal();
        }

        public void MailNext(uint pos, Mail mail)
        {
            MTRetCode mTRetCode = Manager.MailNext(pos, mail.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void MailDelete(uint pos)
        {
            MTRetCode mTRetCode = Manager.MailDelete(pos);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void MailDeleteId(ulong id)
        {
            MTRetCode mTRetCode = Manager.MailDeleteId(id);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void MailSend(Mail mail)
        {
            MTRetCode mTRetCode = Manager.MailSend(mail.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public Mail MailBodyRequest(ulong id)
        {
            CIMTMail cIMTMail = Manager.MailCreate();
            MTRetCode mTRetCode = Manager.MailBodyRequest(id, cIMTMail);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new Mail(cIMTMail, this);
        }

        public void NewsSubscribe(CIMTNewsSink sink)
        {
            MTRetCode mTRetCode = Manager.NewsSubscribe(sink);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void NewsUnsubscribe(CIMTNewsSink sink)
        {
            MTRetCode mTRetCode = Manager.NewsUnsubscribe(sink);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public uint NewsTotal()
        {
            return Manager.NewsTotal();
        }

        public void NewsNext(uint pos, News news)
        {
            MTRetCode mTRetCode = Manager.NewsNext(pos, news.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public News NewsBodyRequest(ulong id)
        {
            CIMTNews cIMTNews = Manager.NewsCreate();
            MTRetCode mTRetCode = Manager.NewsBodyRequest(id, cIMTNews);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new News(cIMTNews, this);
        }

        public void NewsSend(News news)
        {
            MTRetCode mTRetCode = Manager.NewsSend(news.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void RequestSubscribe(CIMTRequestSink sink)
        {
            MTRetCode mTRetCode = Manager.RequestSubscribe(sink);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void RequestUnsubscribe(CIMTRequestSink sink)
        {
            MTRetCode mTRetCode = Manager.RequestUnsubscribe(sink);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public uint RequestTotal()
        {
            return Manager.RequestTotal();
        }

        public Request RequestNext(uint pos)
        {
            CIMTRequest cIMTRequest = Manager.RequestCreate();
            MTRetCode mTRetCode = Manager.RequestNext(pos, cIMTRequest);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new Request(cIMTRequest, this);
        }

        public Request RequestGet(uint id)
        {
            CIMTRequest cIMTRequest = Manager.RequestCreate();
            MTRetCode mTRetCode = Manager.RequestGet(id, cIMTRequest);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new Request(cIMTRequest, this);
        }

        public Request[] RequestGetAll()
        {
            CIMTRequestArray cIMTRequestArray = Manager.RequestCreateArray();
            MTRetCode mTRetCode = Manager.RequestGetAll(cIMTRequestArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Request[])ConvertToNetArray(cIMTRequestArray);
        }

        public void DealerUnsubscribe(CIMTDealerSink sink)
        {
            MTRetCode mTRetCode = Manager.DealerUnsubscribe(sink);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void DealerStop()
        {
            MTRetCode mTRetCode = Manager.DealerStop();
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void DealerGet(Request request)
        {
            MTRetCode mTRetCode = Manager.DealerGet(request.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void DealerLock(uint id, Request request)
        {
            MTRetCode mTRetCode = Manager.DealerLock(id, request.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void DealerAnswer(Confirm confirm)
        {
            MTRetCode mTRetCode = Manager.DealerAnswer(confirm.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void DealerSend(Request request, CIMTDealerSink sink, out uint id)
        {
            MTRetCode mTRetCode = Manager.DealerSend(request.GetRoot(Manager), sink, out id);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void DealerSend(CIMTRequest request, CIMTDealerSink sink, out uint id)
        {
            MTRetCode mTRetCode = Manager.DealerSend(request, sink, out id);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void DealerBalance(ulong login, double value, uint type, string comment, out ulong deal_id)
        {
            MTRetCode mTRetCode = Manager.DealerBalance(login, value, type, comment, out deal_id);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void DealerBalanceRaw(ulong login, double value, uint type, string comment, out ulong deal_id)
        {
            MTRetCode mTRetCode = Manager.DealerBalanceRaw(login, value, type, comment, out deal_id);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public byte[] CustomCommand(byte[] indata, out MTRetCode res)
        {
            return Manager.CustomCommand(indata, out res);
        }

        public void SummarySubscribe(CIMTSummarySink sink)
        {
            MTRetCode mTRetCode = Manager.SummarySubscribe(sink);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void SummaryUnsubscribe(CIMTSummarySink sink)
        {
            MTRetCode mTRetCode = Manager.SummaryUnsubscribe(sink);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void SummaryCurrency(string currency)
        {
            MTRetCode mTRetCode = Manager.SummaryCurrency(currency);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public string SummaryCurrency()
        {
            return Manager.SummaryCurrency();
        }

        public uint SummaryTotal()
        {
            return Manager.SummaryTotal();
        }

        public void SummaryNext(uint pos, Summary summary)
        {
            MTRetCode mTRetCode = Manager.SummaryNext(pos, summary.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public Summary SummaryGet(string symbol)
        {
            CIMTSummary cIMTSummary = Manager.SummaryCreate();
            MTRetCode mTRetCode = Manager.SummaryGet(symbol, cIMTSummary);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new Summary(cIMTSummary, this);
        }

        public Summary[] SummaryGetAll()
        {
            CIMTSummaryArray cIMTSummaryArray = Manager.SummaryCreateArray();
            MTRetCode mTRetCode = Manager.SummaryGetAll(cIMTSummaryArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Summary[])ConvertToNetArray(cIMTSummaryArray);
        }

        public void ExposureSubscribe(CIMTExposureSink sink)
        {
            MTRetCode mTRetCode = Manager.ExposureSubscribe(sink);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void ExposureUnsubscribe(CIMTExposureSink sink)
        {
            MTRetCode mTRetCode = Manager.ExposureUnsubscribe(sink);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void ExposureCurrency(string currency)
        {
            MTRetCode mTRetCode = Manager.ExposureCurrency(currency);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public string ExposureCurrency()
        {
            return Manager.ExposureCurrency();
        }

        public uint ExposureTotal()
        {
            return Manager.ExposureTotal();
        }

        public Exposure ExposureNext(uint pos)
        {
            CIMTExposure cIMTExposure = Manager.ExposureCreate();
            MTRetCode mTRetCode = Manager.ExposureNext(pos, cIMTExposure);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new Exposure(cIMTExposure, this);
        }

        public Exposure ExposureGet(string symbol)
        {
            CIMTExposure cIMTExposure = Manager.ExposureCreate();
            MTRetCode mTRetCode = Manager.ExposureGet(symbol, cIMTExposure);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new Exposure(cIMTExposure, this);
        }

        public Exposure[] ExposureGetAll()
        {
            CIMTExposureArray cIMTExposureArray = Manager.ExposureCreateArray();
            MTRetCode mTRetCode = Manager.ExposureGetAll(cIMTExposureArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Exposure[])ConvertToNetArray(cIMTExposureArray);
        }

        public User UserExternalGet(string account)
        {
            CIMTUser cIMTUser = Manager.UserCreate();
            MTRetCode mTRetCode = Manager.UserExternalGet(account, cIMTUser);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new User(cIMTUser, this);
        }

        public User UserExternalGet(ulong gateway_id, string account)
        {
            CIMTUser cIMTUser = Manager.UserCreate();
            MTRetCode mTRetCode = Manager.UserExternalGet(gateway_id, account, cIMTUser);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new User(cIMTUser, this);
        }

        public User UserExternalRequest(string account)
        {
            CIMTUser cIMTUser = Manager.UserCreate();
            MTRetCode mTRetCode = Manager.UserExternalRequest(account, cIMTUser);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new User(cIMTUser, this);
        }

        public User UserExternalRequest(ulong gateway_id, string account)
        {
            CIMTUser cIMTUser = Manager.UserCreate();
            MTRetCode mTRetCode = Manager.UserExternalRequest(gateway_id, account, cIMTUser);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new User(cIMTUser, this);
        }

        public User[] UserRequestArray(string group)
        {
            CIMTUserArray cIMTUserArray = Manager.UserCreateArray();
            MTRetCode mTRetCode = Manager.UserRequestArray(group, cIMTUserArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (User[])ConvertToNetArray(cIMTUserArray);
        }

        public Account[] UserAccountRequestArray(string group)
        {
            CIMTAccountArray cIMTAccountArray = Manager.UserCreateAccountArray();
            MTRetCode mTRetCode = Manager.UserAccountRequestArray(group, cIMTAccountArray);
            if (group.Trim() == "*" && mTRetCode == MTRetCode.MT_RET_ERR_NOTFOUND)
            {
                return new Account[0];
            }

            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Account[])ConvertToNetArray(cIMTAccountArray);
        }

        public void PasswordChange(uint type, string password)
        {
            MTRetCode mTRetCode = Manager.PasswordChange(type, password);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public Daily[] DailyRequest(ulong login, long from, long to)
        {
            CIMTDailyArray cIMTDailyArray = Manager.DailyCreateArray();
            MTRetCode mTRetCode = Manager.DailyRequest(login, from, to, cIMTDailyArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Daily[])ConvertToNetArray(cIMTDailyArray);
        }

        public Daily[] DailyRequestLight(ulong login, long from, long to)
        {
            CIMTDailyArray cIMTDailyArray = Manager.DailyCreateArray();
            MTRetCode mTRetCode = Manager.DailyRequestLight(login, from, to, cIMTDailyArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Daily[])ConvertToNetArray(cIMTDailyArray);
        }

        public Daily[] DailyRequestByGroup(string mask, long from, long to)
        {
            CIMTDailyArray cIMTDailyArray = Manager.DailyCreateArray();
            MTRetCode mTRetCode = Manager.DailyRequestByGroup(mask, from, to, cIMTDailyArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Daily[])ConvertToNetArray(cIMTDailyArray);
        }

        public Daily[] DailyRequestByLogins(ulong[] logins, long from, long to)
        {
            CIMTDailyArray cIMTDailyArray = Manager.DailyCreateArray();
            MTRetCode mTRetCode = Manager.DailyRequestByLogins(logins, from, to, cIMTDailyArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Daily[])ConvertToNetArray(cIMTDailyArray);
        }

        public Daily[] DailyRequestLightByGroup(string mask, long from, long to)
        {
            CIMTDailyArray cIMTDailyArray = Manager.DailyCreateArray();
            MTRetCode mTRetCode = Manager.DailyRequestLightByGroup(mask, from, to, cIMTDailyArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Daily[])ConvertToNetArray(cIMTDailyArray);
        }

        public Daily[] DailyRequestLightByLogins(ulong[] logins, long from, long to)
        {
            CIMTDailyArray cIMTDailyArray = Manager.DailyCreateArray();
            MTRetCode mTRetCode = Manager.DailyRequestLightByLogins(logins, from, to, cIMTDailyArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Daily[])ConvertToNetArray(cIMTDailyArray);
        }

        public void PluginUpdate(ConPlugin plugin)
        {
            MTRetCode mTRetCode = Manager.PluginUpdate(plugin.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public uint PluginTotal()
        {
            return Manager.PluginTotal();
        }

        public void PluginNext(uint pos, ConPlugin plugin)
        {
            MTRetCode mTRetCode = Manager.PluginNext(pos, plugin.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public ConPlugin PluginGet(string name)
        {
            CIMTConPlugin cIMTConPlugin = Manager.PluginCreate();
            MTRetCode mTRetCode = Manager.PluginGet(name, cIMTConPlugin);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new ConPlugin(cIMTConPlugin, this);
        }

        public Deal[] DealRequest(ulong login, long from, long to)
        {
            CIMTDealArray cIMTDealArray = Manager.DealCreateArray();
            MTRetCode mTRetCode = Manager.DealRequest(login, from, to, cIMTDealArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Deal[])ConvertToNetArray(cIMTDealArray);
        }

        public Deal DealRequest(ulong ticket)
        {
            CIMTDeal cIMTDeal = Manager.DealCreate();
            MTRetCode mTRetCode = Manager.DealRequest(ticket, cIMTDeal);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new Deal(cIMTDeal, this);
        }

        public void DealSubscribe(CIMTDealSink sink)
        {
            MTRetCode mTRetCode = Manager.DealSubscribe(sink);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void DealUnsubscribe(CIMTDealSink sink)
        {
            MTRetCode mTRetCode = Manager.DealUnsubscribe(sink);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void DealDelete(ulong ticket)
        {
            MTRetCode mTRetCode = Manager.DealDelete(ticket);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void DealUpdate(Deal deal)
        {
            MTRetCode mTRetCode = Manager.DealUpdate(deal.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public Deal[] DealRequestByGroup(string mask, long from, long to)
        {
            CIMTDealArray cIMTDealArray = Manager.DealCreateArray();
            MTRetCode mTRetCode = Manager.DealRequestByGroup(mask, from, to, cIMTDealArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Deal[])ConvertToNetArray(cIMTDealArray);
        }

        public Deal[] DealRequestByLogins(ulong[] logins, long from, long to)
        {
            CIMTDealArray cIMTDealArray = Manager.DealCreateArray();
            MTRetCode mTRetCode = Manager.DealRequestByLogins(logins, from, to, cIMTDealArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Deal[])ConvertToNetArray(cIMTDealArray);
        }

        public Deal[] DealRequestByTickets(ulong[] tickets)
        {
            CIMTDealArray cIMTDealArray = Manager.DealCreateArray();
            MTRetCode mTRetCode = Manager.DealRequestByTickets(tickets, cIMTDealArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Deal[])ConvertToNetArray(cIMTDealArray);
        }

        public Deal[] DealRequestPage(ulong login, long from, long to, uint offset, uint total)
        {
            CIMTDealArray cIMTDealArray = Manager.DealCreateArray();
            MTRetCode mTRetCode = Manager.DealRequestPage(login, from, to, offset, total, cIMTDealArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Deal[])ConvertToNetArray(cIMTDealArray);
        }

        public MTRetCode[] DealUpdateBatch(Deal[] deals)
        {
            MTRetCode[] array = new MTRetCode[deals.Length];
            MTRetCode mTRetCode = Manager.DealUpdateBatch((CIMTDealArray)ConvertFromNetArray(deals, "Deal[]", "CIMTDealArray"), array);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return array;
        }

        public MTRetCode[] DealDeleteBatch(ulong[] tickets)
        {
            MTRetCode[] array = new MTRetCode[tickets.Length];
            MTRetCode mTRetCode = Manager.DealDeleteBatch(tickets, array);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return array;
        }

        public void DealAdd(Deal deal)
        {
            MTRetCode mTRetCode = Manager.DealAdd(deal.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public MTRetCode[] DealAddBatch(Deal[] deals)
        {
            MTRetCode[] array = new MTRetCode[deals.Length];
            MTRetCode mTRetCode = Manager.DealAddBatch((CIMTDealArray)ConvertFromNetArray(deals, "Deal[]", "CIMTDealArray"), array);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return array;
        }

        public void DealPerform(Deal deal)
        {
            MTRetCode mTRetCode = Manager.DealPerform(deal.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public MTRetCode[] DealPerformBatch(Deal[] deals)
        {
            MTRetCode[] array = new MTRetCode[deals.Length];
            MTRetCode mTRetCode = Manager.DealPerformBatch((CIMTDealArray)ConvertFromNetArray(deals, "Deal[]", "CIMTDealArray"), array);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return array;
        }

        public void PositionSubscribe(CIMTPositionSink sink)
        {
            MTRetCode mTRetCode = Manager.PositionSubscribe(sink);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void PositionUnsubscribe(CIMTPositionSink sink)
        {
            MTRetCode mTRetCode = Manager.PositionUnsubscribe(sink);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public Position[] PositionGet(ulong login)
        {
            CIMTPositionArray cIMTPositionArray = Manager.PositionCreateArray();
            MTRetCode mTRetCode = Manager.PositionGet(login, cIMTPositionArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Position[])ConvertToNetArray(cIMTPositionArray);
        }

        public Position PositionGet(ulong login, string symbol)
        {
            CIMTPosition cIMTPosition = Manager.PositionCreate();
            MTRetCode mTRetCode = Manager.PositionGet(login, symbol, cIMTPosition);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new Position(cIMTPosition, this);
        }

        public Position[] PositionRequest(ulong login)
        {
            CIMTPositionArray cIMTPositionArray = Manager.PositionCreateArray();
            MTRetCode mTRetCode = Manager.PositionRequest(login, cIMTPositionArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Position[])ConvertToNetArray(cIMTPositionArray);
        }

        public Position PositionGetByTicket(ulong ticket)
        {
            CIMTPosition cIMTPosition = Manager.PositionCreate();
            MTRetCode mTRetCode = Manager.PositionGetByTicket(ticket, cIMTPosition);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new Position(cIMTPosition, this);
        }

        public void PositionDelete(Position position)
        {
            MTRetCode mTRetCode = Manager.PositionDelete(position.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void PositionUpdate(Position position)
        {
            MTRetCode mTRetCode = Manager.PositionUpdate(position.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public Position[] PositionGetByGroup(string mask)
        {
            CIMTPositionArray cIMTPositionArray = Manager.PositionCreateArray();
            MTRetCode mTRetCode = Manager.PositionGetByGroup(mask, cIMTPositionArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Position[])ConvertToNetArray(cIMTPositionArray);
        }

        public Position[] PositionGetByLogins(ulong[] logins)
        {
            CIMTPositionArray cIMTPositionArray = Manager.PositionCreateArray();
            MTRetCode mTRetCode = Manager.PositionGetByLogins(logins, cIMTPositionArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Position[])ConvertToNetArray(cIMTPositionArray);
        }

        public Position[] PositionGetByTickets(ulong[] tickets)
        {
            CIMTPositionArray cIMTPositionArray = Manager.PositionCreateArray();
            MTRetCode mTRetCode = Manager.PositionGetByTickets(tickets, cIMTPositionArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Position[])ConvertToNetArray(cIMTPositionArray);
        }

        public void PositionDeleteByTicket(ulong ticket)
        {
            MTRetCode mTRetCode = Manager.PositionDeleteByTicket(ticket);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public Position[] PositionRequestByGroup(string mask)
        {
            CIMTPositionArray cIMTPositionArray = Manager.PositionCreateArray();
            MTRetCode mTRetCode = Manager.PositionRequestByGroup(mask, cIMTPositionArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Position[])ConvertToNetArray(cIMTPositionArray);
        }

        public Position[] PositionRequestByLogins(ulong[] logins)
        {
            CIMTPositionArray cIMTPositionArray = Manager.PositionCreateArray();
            MTRetCode mTRetCode = Manager.PositionRequestByLogins(logins, cIMTPositionArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Position[])ConvertToNetArray(cIMTPositionArray);
        }

        public Position[] PositionRequestByTickets(ulong[] tickets)
        {
            CIMTPositionArray cIMTPositionArray = Manager.PositionCreateArray();
            MTRetCode mTRetCode = Manager.PositionRequestByTickets(tickets, cIMTPositionArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Position[])ConvertToNetArray(cIMTPositionArray);
        }

        public MTRetCode[] PositionUpdateBatch(Position[] positions)
        {
            MTRetCode[] array = new MTRetCode[positions.Length];
            MTRetCode mTRetCode = Manager.PositionUpdateBatch((CIMTPositionArray)ConvertFromNetArray(positions, "Position[]", "CIMTPositionArray"), array);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return array;
        }

        public MTRetCode[] PositionDeleteBatch(ulong[] tickets)
        {
            MTRetCode[] array = new MTRetCode[tickets.Length];
            MTRetCode mTRetCode = Manager.PositionDeleteBatch(tickets, array);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return array;
        }

        public MTRetCode[] PositionSplit(ulong[] tickets, double[] adjustments, uint new_shares, uint old_shares, uint round_rule_price, uint round_rule_volume, uint flags)
        {
            MTRetCode[] array = new MTRetCode[tickets.Length];
            MTRetCode mTRetCode = Manager.PositionSplit(tickets, adjustments, new_shares, old_shares, round_rule_price, round_rule_volume, flags, array);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return array;
        }

        public Position[] PositionGetBySymbol(string groups, string symbol)
        {
            CIMTPositionArray cIMTPositionArray = Manager.PositionCreateArray();
            MTRetCode mTRetCode = Manager.PositionGetBySymbol(groups, symbol, cIMTPositionArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Position[])ConvertToNetArray(cIMTPositionArray);
        }

        public void OrderSubscribe(CIMTOrderSink sink)
        {
            MTRetCode mTRetCode = Manager.OrderSubscribe(sink);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void OrderUnsubscribe(CIMTOrderSink sink)
        {
            MTRetCode mTRetCode = Manager.OrderUnsubscribe(sink);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public Order OrderGet(ulong ticket)
        {
            CIMTOrder cIMTOrder = Manager.OrderCreate();
            MTRetCode mTRetCode = Manager.OrderGet(ticket, cIMTOrder);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new Order(cIMTOrder, this);
        }

        public Order[] OrderGetOpen(ulong login)
        {
            CIMTOrderArray cIMTOrderArray = Manager.OrderCreateArray();
            MTRetCode mTRetCode = Manager.OrderGetOpen(login, cIMTOrderArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Order[])ConvertToNetArray(cIMTOrderArray);
        }

        public Order OrderRequest(ulong ticket)
        {
            CIMTOrder cIMTOrder = Manager.OrderCreate();
            MTRetCode mTRetCode = Manager.OrderRequest(ticket, cIMTOrder);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new Order(cIMTOrder, this);
        }

        public Order[] OrderRequestOpen(ulong login)
        {
            CIMTOrderArray cIMTOrderArray = Manager.OrderCreateArray();
            MTRetCode mTRetCode = Manager.OrderRequestOpen(login, cIMTOrderArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Order[])ConvertToNetArray(cIMTOrderArray);
        }

        public void OrderDelete(ulong ticket)
        {
            MTRetCode mTRetCode = Manager.OrderDelete(ticket);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void OrderUpdate(Order order)
        {
            MTRetCode mTRetCode = Manager.OrderUpdate(order.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public Order[] OrderGetByGroup(string mask)
        {
            CIMTOrderArray cIMTOrderArray = Manager.OrderCreateArray();
            MTRetCode mTRetCode = Manager.OrderGetByGroup(mask, cIMTOrderArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Order[])ConvertToNetArray(cIMTOrderArray);
        }

        public Order[] OrderGetByLogins(ulong[] logins)
        {
            CIMTOrderArray cIMTOrderArray = Manager.OrderCreateArray();
            MTRetCode mTRetCode = Manager.OrderGetByLogins(logins, cIMTOrderArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Order[])ConvertToNetArray(cIMTOrderArray);
        }

        public Order[] OrderGetByTickets(ulong[] tickets)
        {
            CIMTOrderArray cIMTOrderArray = Manager.OrderCreateArray();
            MTRetCode mTRetCode = Manager.OrderGetByTickets(tickets, cIMTOrderArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Order[])ConvertToNetArray(cIMTOrderArray);
        }

        public Order[] OrderRequestByGroup(string mask)
        {
            CIMTOrderArray cIMTOrderArray = Manager.OrderCreateArray();
            MTRetCode mTRetCode = Manager.OrderRequestByGroup(mask, cIMTOrderArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Order[])ConvertToNetArray(cIMTOrderArray);
        }

        public Order[] OrderRequestByLogins(ulong[] logins)
        {
            CIMTOrderArray cIMTOrderArray = Manager.OrderCreateArray();
            MTRetCode mTRetCode = Manager.OrderRequestByLogins(logins, cIMTOrderArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Order[])ConvertToNetArray(cIMTOrderArray);
        }

        public Order[] OrderRequestByTickets(ulong[] tickets)
        {
            CIMTOrderArray cIMTOrderArray = Manager.OrderCreateArray();
            MTRetCode mTRetCode = Manager.OrderRequestByTickets(tickets, cIMTOrderArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Order[])ConvertToNetArray(cIMTOrderArray);
        }

        public MTRetCode[] OrderUpdateBatch(Order[] orders)
        {
            MTRetCode[] array = new MTRetCode[orders.Length];
            MTRetCode mTRetCode = Manager.OrderUpdateBatch((CIMTOrderArray)ConvertFromNetArray(orders, "Order[]", "CIMTOrderArray"), array);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return array;
        }

        public MTRetCode[] OrderDeleteBatch(ulong[] tickets)
        {
            MTRetCode[] array = new MTRetCode[tickets.Length];
            MTRetCode mTRetCode = Manager.OrderDeleteBatch(tickets, array);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return array;
        }

        public Order[] OrderGetBySymbol(string groups, string symbol)
        {
            CIMTOrderArray cIMTOrderArray = Manager.OrderCreateArray();
            MTRetCode mTRetCode = Manager.OrderGetBySymbol(groups, symbol, cIMTOrderArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Order[])ConvertToNetArray(cIMTOrderArray);
        }

        public void OrderAdd(Order order)
        {
            MTRetCode mTRetCode = Manager.OrderAdd(order.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public MTRetCode[] OrderAddBatch(Order[] orders)
        {
            MTRetCode[] array = new MTRetCode[orders.Length];
            MTRetCode mTRetCode = Manager.OrderAddBatch((CIMTOrderArray)ConvertFromNetArray(orders, "Order[]", "CIMTOrderArray"), array);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return array;
        }

        private object ConvertFromNetArray(object orders, string from, string to)
        {
            throw new NotImplementedException();
        }

        public Order[] HistoryRequest(ulong login, long from, long to)
        {
            CIMTOrderArray cIMTOrderArray = Manager.OrderCreateArray();
            MTRetCode mTRetCode = Manager.HistoryRequest(login, from, to, cIMTOrderArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Order[])ConvertToNetArray(cIMTOrderArray);
        }

        public Order[] HistoryRequestByGroup(string group, long from, long to)
        {
            CIMTOrderArray cIMTOrderArray = Manager.OrderCreateArray();
            MTRetCode mTRetCode = Manager.HistoryRequestByGroup(group, from, to, cIMTOrderArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Order[])ConvertToNetArray(cIMTOrderArray);
        }

        public Order[] HistoryRequestByLogins(ulong[] logins, long from, long to)
        {
            CIMTOrderArray cIMTOrderArray = Manager.OrderCreateArray();
            MTRetCode mTRetCode = Manager.HistoryRequestByLogins(logins, from, to, cIMTOrderArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Order[])ConvertToNetArray(cIMTOrderArray);
        }

        public Order[] HistoryRequestByTickets(ulong[] tickets)
        {
            CIMTOrderArray cIMTOrderArray = Manager.OrderCreateArray();
            MTRetCode mTRetCode = Manager.HistoryRequestByTickets(tickets, cIMTOrderArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Order[])ConvertToNetArray(cIMTOrderArray);
        }

        public Order[] HistoryRequestPage(ulong login, long from, long to, uint offset, uint total)
        {
            CIMTOrderArray cIMTOrderArray = Manager.OrderCreateArray();
            MTRetCode mTRetCode = Manager.HistoryRequestPage(login, from, to, offset, total, cIMTOrderArray);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return (Order[])ConvertToNetArray(cIMTOrderArray);
        }

        private object ConvertToNetArray(object array)
        {
            if (array is CIMTPositionArray)
            {
                CIMTPositionArray cIMTPositionArray = (CIMTPositionArray)array;
                CIMTPosition[] array2 = cIMTPositionArray.ToArray();
                Position[] array3 = new Position[array2.Length];
                for (int i = 0; i < array2.Length; i++)
                {
                    array3[i] = new Position(array2[i], this);
                }

                return array3;
            }

            if (array is CIMTOrderArray)
            {
                CIMTOrderArray cIMTOrderArray = (CIMTOrderArray)array;
                CIMTOrder[] array4 = cIMTOrderArray.ToArray();
                Order[] array5 = new Order[array4.Length];
                for (int j = 0; j < array4.Length; j++)
                {
                    array5[j] = new Order(array4[j], this);
                }

                return array5;
            }

            if (array is CIMTDealArray)
            {
                CIMTDealArray cIMTDealArray = (CIMTDealArray)array;
                CIMTDeal[] array6 = cIMTDealArray.ToArray();
                Deal[] array7 = new Deal[array6.Length];
                for (int k = 0; k < array6.Length; k++)
                {
                    array7[k] = new Deal(array6[k], this);
                }

                return array7;
            }

            if (array is CIMTAccountArray)
            {
                CIMTAccountArray cIMTAccountArray = (CIMTAccountArray)array;
                CIMTAccount[] array8 = cIMTAccountArray.ToArray();
                Account[] array9 = new Account[array8.Length];
                for (int l = 0; l < array8.Length; l++)
                {
                    array9[l] = new Account(array8[l], this);
                }

                return array9;
            }

            if (array is CIMTUserArray)
            {
                CIMTUserArray cIMTUserArray = (CIMTUserArray)array;
                CIMTUser[] array10 = cIMTUserArray.ToArray();
                User[] array11 = new User[array10.Length];
                for (int m = 0; m < array10.Length; m++)
                {
                    array11[m] = new User(array10[m], this);
                }

                return array11;
            }

            throw new NotImplementedException();
        }

        public void TickSubscribe(CIMTTickSink sink)
        {
            MTRetCode mTRetCode = Manager.TickSubscribe(sink);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void TickUnsubscribe(CIMTTickSink sink)
        {
            MTRetCode mTRetCode = Manager.TickUnsubscribe(sink);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void TickAdd(string symbol, MetaQuotes.MT5CommonAPI.MTTick tick)
        {
            MTRetCode mTRetCode = Manager.TickAdd(symbol, tick);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void TickAddStat(MetaQuotes.MT5CommonAPI.MTTick tick, MetaQuotes.MT5CommonAPI.MTTickStat stat)
        {
            MTRetCode mTRetCode = Manager.TickAddStat(tick, stat);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public MetaQuotes.MT5CommonAPI.MTTickShort TickLast(string symbol, string group)
        {
            MetaQuotes.MT5CommonAPI.MTTickShort tick;
            MTRetCode mTRetCode = Manager.TickLast(symbol, group, out tick);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return tick;
        }

        public MetaQuotes.MT5CommonAPI.MTTick[] TickLast(ref uint id)
        {
            MTRetCode res;
            MetaQuotes.MT5CommonAPI.MTTick[] result = Manager.TickLast(ref id, out res);
            if (res != 0)
            {
                throw new ApiException(res);
            }

            return result;
        }

        public MetaQuotes.MT5CommonAPI.MTTickShort TickLast(string symbol)
        {
            MetaQuotes.MT5CommonAPI.MTTickShort tick;
            MTRetCode mTRetCode = Manager.TickLast(symbol, out tick);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return tick;
        }

        public MetaQuotes.MT5CommonAPI.MTTickStat TickStat(string symbol)
        {
            MetaQuotes.MT5CommonAPI.MTTickStat stat;
            MTRetCode mTRetCode = Manager.TickStat(symbol, out stat);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return stat;
        }

        public MetaQuotes.MT5CommonAPI.MTTickShort[] TickHistoryRequest(string symbol, long from, long to)
        {
            MTRetCode res;
            MetaQuotes.MT5CommonAPI.MTTickShort[] result = Manager.TickHistoryRequest(symbol, from, to, out res);
            if (res != 0)
            {
                throw new ApiException(res);
            }

            return result;
        }

        public void TickLastRaw(string symbol, out MetaQuotes.MT5CommonAPI.MTTickShort tick)
        {
            MTRetCode mTRetCode = Manager.TickLastRaw(symbol, out tick);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void TickAddBatch(MetaQuotes.MT5CommonAPI.MTTick[] ticks)
        {
            MTRetCode mTRetCode = Manager.TickAddBatch(ticks);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void BookSubscribe(string symbol, CIMTBookSink sink)
        {
            MTRetCode mTRetCode = Manager.BookSubscribe(symbol, sink);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void BookUnsubscribe(string symbol, CIMTBookSink sink)
        {
            MTRetCode mTRetCode = Manager.BookUnsubscribe(symbol, sink);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public MetaQuotes.MT5CommonAPI.MTBook BookGet(string symbol)
        {
            MetaQuotes.MT5CommonAPI.MTBook res;
            MTRetCode mTRetCode = Manager.BookGet(symbol, out res);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return res;
        }

        public void Release()
        {
            Manager.Release();
        }

        public void LicenseCheck(ref MetaQuotes.MT5CommonAPI.MTLicenseCheck check)
        {
            MTRetCode mTRetCode = Manager.LicenseCheck(ref check);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void ProxySet(MetaQuotes.MT5ManagerAPI.MTProxyInfo proxy)
        {
            MTRetCode mTRetCode = Manager.ProxySet(proxy);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void Connect(string server, ulong login, string password, string password_cert, CIMTManagerAPI.EnPumpModes pump_mode, uint timeout)
        {
            MTRetCode mTRetCode = Manager.Connect(server, login, password, password_cert, pump_mode, timeout);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void Disconnect()
        {
            Manager.Disconnect();
        }

        public void LoggerOut(EnMTLogCode code, string format, object[] args)
        {
            MTRetCode mTRetCode = Manager.LoggerOut(code, format, args);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public MetaQuotes.MT5CommonAPI.MTLogRecord[] LoggerServerRequest(EnMTLogRequestMode mode, EnMTLogType type, long from, long to, string filter)
        {
            MTRetCode res;
            MetaQuotes.MT5CommonAPI.MTLogRecord[] result = Manager.LoggerServerRequest(mode, type, from, to, filter, out res);
            if (res != 0)
            {
                throw new ApiException(res);
            }

            return result;
        }

        public void TimeSubscribe(CIMTConTimeSink obj)
        {
            MTRetCode mTRetCode = Manager.TimeSubscribe(obj);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void TimeUnsubscribe(CIMTConTimeSink obj)
        {
            MTRetCode mTRetCode = Manager.TimeUnsubscribe(obj);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void TimeGet(ConTime obj)
        {
            MTRetCode mTRetCode = Manager.TimeGet(obj.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public long TimeServer()
        {
            return Manager.TimeServer();
        }

        public void TimeServerRequest(out long time_msc)
        {
            MTRetCode mTRetCode = Manager.TimeServerRequest(out time_msc);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void HolidaySubscribe(CIMTConHolidaySink sink)
        {
            MTRetCode mTRetCode = Manager.HolidaySubscribe(sink);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void HolidayUnsubscribe(CIMTConHolidaySink sink)
        {
            MTRetCode mTRetCode = Manager.HolidayUnsubscribe(sink);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public uint HolidayTotal()
        {
            return Manager.HolidayTotal();
        }

        public void HolidayNext(uint pos, ConHoliday obj)
        {
            MTRetCode mTRetCode = Manager.HolidayNext(pos, obj.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void ManagerCurrent(ConManager manager)
        {
            MTRetCode mTRetCode = Manager.ManagerCurrent(manager.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void GroupSubscribe(CIMTConGroupSink sink)
        {
            MTRetCode mTRetCode = Manager.GroupSubscribe(sink);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void GroupUnsubscribe(CIMTConGroupSink sink)
        {
            MTRetCode mTRetCode = Manager.GroupUnsubscribe(sink);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public uint GroupTotal()
        {
            return Manager.GroupTotal();
        }

        public void GroupNext(uint pos, ConGroup group)
        {
            MTRetCode mTRetCode = Manager.GroupNext(pos, group.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public ConGroup GroupGet(string name)
        {
            CIMTConGroup cIMTConGroup = Manager.GroupCreate();
            MTRetCode mTRetCode = Manager.GroupGet(name, cIMTConGroup);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new ConGroup(cIMTConGroup, this);
        }

        public ConGroup GroupRequest(string name)
        {
            CIMTConGroup cIMTConGroup = Manager.GroupCreate();
            MTRetCode mTRetCode = Manager.GroupRequest(name, cIMTConGroup);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new ConGroup(cIMTConGroup, this);
        }

        public void GroupUpdate(ConGroup group)
        {
            MTRetCode mTRetCode = Manager.GroupUpdate(group.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public MTRetCode[] GroupUpdateBatch(ConGroup[] groups)
        {
            MTRetCode[] array = new MTRetCode[groups.Length];
            MTRetCode mTRetCode = Manager.GroupUpdateBatch(ConvertArray(groups), array);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return array;
        }

        private CIMTConGroup[] ConvertArray(ConGroup[] groups)
        {
            throw new NotImplementedException();
        }

        public void SymbolSubscribe(CIMTConSymbolSink obj)
        {
            MTRetCode mTRetCode = Manager.SymbolSubscribe(obj);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void SymbolUnsubscribe(CIMTConSymbolSink obj)
        {
            MTRetCode mTRetCode = Manager.SymbolUnsubscribe(obj);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public uint SymbolTotal()
        {
            return Manager.SymbolTotal();
        }

        public ConSymbol SymbolNext(uint pos)
        {
            CIMTConSymbol cIMTConSymbol = Manager.SymbolCreate();
            MTRetCode mTRetCode = Manager.SymbolNext(pos, cIMTConSymbol);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new ConSymbol(cIMTConSymbol, this);
        }

        public ConSymbol SymbolGet(string name, string group)
        {
            CIMTConSymbol cIMTConSymbol = Manager.SymbolCreate();
            MTRetCode mTRetCode = Manager.SymbolGet(name, group, cIMTConSymbol);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new ConSymbol(cIMTConSymbol, this);
        }

        public ConSymbol SymbolGet(string name)
        {
            CIMTConSymbol cIMTConSymbol = Manager.SymbolCreate();
            MTRetCode mTRetCode = Manager.SymbolGet(name, cIMTConSymbol);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new ConSymbol(cIMTConSymbol, this);
        }

        public ConSymbol SymbolRequest(string name, string group)
        {
            CIMTConSymbol cIMTConSymbol = Manager.SymbolCreate();
            MTRetCode mTRetCode = Manager.SymbolRequest(name, group, cIMTConSymbol);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new ConSymbol(cIMTConSymbol, this);
        }

        public ConSymbol SymbolRequest(string name)
        {
            CIMTConSymbol cIMTConSymbol = Manager.SymbolCreate();
            MTRetCode mTRetCode = Manager.SymbolRequest(name, cIMTConSymbol);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new ConSymbol(cIMTConSymbol, this);
        }

        public void SymbolExist(ConSymbol symbol, ConGroup group)
        {
            MTRetCode mTRetCode = Manager.SymbolExist(symbol.GetRoot(Manager), group.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void SymbolUpdate(ConSymbol symbol)
        {
            MTRetCode mTRetCode = Manager.SymbolUpdate(symbol.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public MTRetCode[] SymbolUpdateBatch(ConSymbol[] symbols)
        {
            MTRetCode[] array = new MTRetCode[symbols.Length];
            MTRetCode mTRetCode = Manager.SymbolUpdateBatch(ConvertArray(symbols), array);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return array;
        }

        private CIMTConSymbol[] ConvertArray(ConSymbol[] symbols)
        {
            throw new NotImplementedException();
        }

        public void SelectedAdd(string symbol)
        {
            MTRetCode mTRetCode = Manager.SelectedAdd(symbol);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void SelectedAddBatch(string[] symbols)
        {
            MTRetCode mTRetCode = Manager.SelectedAddBatch(symbols);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void SelectedAddAll()
        {
            MTRetCode mTRetCode = Manager.SelectedAddAll();
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void SelectedDelete(uint pos)
        {
            MTRetCode mTRetCode = Manager.SelectedDelete(pos);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void SelectedDelete(string symbol)
        {
            MTRetCode mTRetCode = Manager.SelectedDelete(symbol);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void SelectedDeleteBatch(string[] symbols)
        {
            MTRetCode mTRetCode = Manager.SelectedDeleteBatch(symbols);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void SelectedDeleteAll()
        {
            MTRetCode mTRetCode = Manager.SelectedDeleteAll();
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void SelectedShift(uint pos, int shift)
        {
            MTRetCode mTRetCode = Manager.SelectedShift(pos, shift);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public uint SelectedTotal()
        {
            return Manager.SelectedTotal();
        }

        public void SelectedNext(uint pos, out string symbol)
        {
            MTRetCode mTRetCode = Manager.SelectedNext(pos, out symbol);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public User UserAdd(User user, string master_pass, string investor_pass)
        {
            CIMTUser root = user.GetRoot(Manager);
            MTRetCode mTRetCode = Manager.UserAdd(root, master_pass, investor_pass);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new User(root, this);
        }

        public void UserDelete(ulong login)
        {
            MTRetCode mTRetCode = Manager.UserDelete(login);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void UserUpdate(User obj)
        {
            MTRetCode mTRetCode = Manager.UserUpdate(obj.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public uint UserTotal()
        {
            return Manager.UserTotal();
        }

        public User UserGet(ulong login)
        {
            CIMTUser cIMTUser = Manager.UserCreate();
            MTRetCode mTRetCode = Manager.UserGet(login, cIMTUser);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new User(cIMTUser, this);
        }

        public User UserRequest(ulong login)
        {
            CIMTUser cIMTUser = Manager.UserCreate();
            MTRetCode mTRetCode = Manager.UserRequest(login, cIMTUser);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new User(cIMTUser, this);
        }

        public void UserGroup(ulong login, out string group)
        {
            MTRetCode mTRetCode = Manager.UserGroup(login, out group);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public ulong[] UserLogins(string group, out MTRetCode res)
        {
            return Manager.UserLogins(group, out res);
        }

        public void UserPasswordCheck(CIMTUser.EnUsersPasswords type, ulong login, string password)
        {
            MTRetCode mTRetCode = Manager.UserPasswordCheck(type, login, password);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void UserPasswordChange(CIMTUser.EnUsersPasswords type, ulong login, string password)
        {
            MTRetCode mTRetCode = Manager.UserPasswordChange(type, login, password);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void UserCertDelete(ulong login)
        {
            MTRetCode mTRetCode = Manager.UserCertDelete(login);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void UserCertConfirm(ulong login)
        {
            MTRetCode mTRetCode = Manager.UserCertConfirm(login);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public void UserExternalSync(ulong login, ulong gateway_id, string account_id, CIMTManagerAPI.EnExternalSyncModes sync_mode)
        {
            MTRetCode mTRetCode = Manager.UserExternalSync(login, gateway_id, account_id, sync_mode);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public Account UserAccountGet(ulong login)
        {
            CIMTAccount cIMTAccount = Manager.UserCreateAccount();
            MTRetCode mTRetCode = Manager.UserAccountGet(login, cIMTAccount);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new Account(cIMTAccount, this);
        }

        public Account UserAccountRequest(ulong login)
        {
            CIMTAccount cIMTAccount = Manager.UserCreateAccount();
            MTRetCode mTRetCode = Manager.UserAccountRequest(login, cIMTAccount);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new Account(cIMTAccount, this);
        }

        public void UserCertUpdate(ulong login, Certificate obj)
        {
            MTRetCode mTRetCode = Manager.UserCertUpdate(login, obj.GetRoot(Manager));
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public Certificate UserCertGet(ulong login)
        {
            CIMTCertificate cIMTCertificate = Manager.UserCertCreate();
            MTRetCode mTRetCode = Manager.UserCertGet(login, cIMTCertificate);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }

            return new Certificate(cIMTCertificate, this);
        }

        public void UserBalanceCheck(ulong login, bool fixflag, out double balance_user, out double balance_history, out double credit_user, out double credit_history)
        {
            MTRetCode mTRetCode = Manager.UserBalanceCheck(login, fixflag, out balance_user, out balance_history, out credit_user, out credit_history);
            if (mTRetCode != 0)
            {
                throw new ApiException(mTRetCode);
            }
        }

        public new Type GetType()
        {
            return Manager.GetType();
        }

        public new string ToString()
        {
            return Manager.ToString();
        }

        public new bool Equals(object obj)
        {
            return Manager.Equals(obj);
        }

        public new int GetHashCode()
        {
            return Manager.GetHashCode();
        }

        internal void CallOnRequestAdd(CIMTRequest request)
        {
            ExecuteEvent(OnRequestAddProc, request);
        }

        internal void CallOnDealerResult(CIMTConfirm result)
        {
            OnDealerResultProc(result);
        }

        internal void CallOnDealerAnswer(CIMTRequest request)
        {
            OnDealerAnswerProc(request);
        }

        internal void CallOnRequestUpdate(CIMTRequest request)
        {
            ExecuteEvent(OnRequestUpdateProc, request);
        }

        internal void CallOnRequestDelete(CIMTRequest request)
        {
            ExecuteEvent(OnRequestDeleteProc, request);
        }

        private void OnRequestAddProc(object args)
        {
            try
            {
                CIMTRequest cIMTRequest = Manager.RequestCreate();
                while (Manager.DealerGet(cIMTRequest) == MTRetCode.MT_RET_OK)
                {
                    CIMTConfirm cIMTConfirm = Manager.DealerConfirmCreate();
                    if (cIMTConfirm == null)
                    {
                        throw new Exception("DealerConfirmCreate fail");
                    }

                    cIMTConfirm.ID(cIMTRequest.ID());
                    if (this.OnRequestAdd != null)
                    {
                        this.OnRequestAdd(this, cIMTRequest, cIMTConfirm);
                    }
                }
            }
            catch (Exception ex)
            {
                Manager.LoggerOut(EnMTLogCode.MTLogErr, "OnRequestAdd client exception: {0}", ex.Message);
            }
        }

        private void OnDealerResultProc(object args)
        {
            try
            {
                if (this.OnDealerResult != null)
                {
                    this.OnDealerResult(this, (CIMTConfirm)args);
                }
            }
            catch (Exception ex)
            {
                Manager.LoggerOut(EnMTLogCode.MTLogErr, "OnDealerResult client exception: {0}", ex.Message);
            }
        }

        private void OnDealerAnswerProc(object args)
        {
            try
            {
                if (this.OnDealerAnswer != null)
                {
                    this.OnDealerAnswer(this, new MTRequest((CIMTRequest)args));
                }
            }
            catch (Exception ex)
            {
                Manager.LoggerOut(EnMTLogCode.MTLogErr, "OnDealerAnswer client exception: {0}", ex.Message);
            }
        }

        private void OnRequestUpdateProc(object args)
        {
            try
            {
                if (this.OnRequestUpdate != null)
                {
                    this.OnRequestUpdate(this, (CIMTRequest)args);
                }
            }
            catch (Exception ex)
            {
                Manager.LoggerOut(EnMTLogCode.MTLogErr, "OnRequestUpdate client exception: {0}", ex.Message);
            }
        }

        private void OnRequestDeleteProc(object args)
        {
            try
            {
                if (this.OnRequestDelete != null)
                {
                    this.OnRequestDelete(this, (CIMTRequest)args);
                }
            }
            catch (Exception ex)
            {
                Manager.LoggerOut(EnMTLogCode.MTLogErr, "OnRequestDelete client exception: {0}", ex.Message);
            }
        }

        internal void CallOnTick(MetaQuotes.MT5CommonAPI.MTTick tick)
        {
            ExecuteEvent(OnTickProc, tick);
        }

        internal void CallOnOrderUpdate(OrderUpdate update)
        {
            ExecuteEvent(OnOrderUpdateProc, update);
        }

        internal void CallOnPositionUpdate(PositionUpdate update)
        {
            ExecuteEvent(OnPositionUpdateProc, update);
        }

        internal void CallOnSummary(Summary summary)
        {
            ExecuteEvent(OnSummaryProc, summary);
        }

        private void OnTickProc(object args)
        {
            this.OnTick?.Invoke(this, (MetaQuotes.MT5CommonAPI.MTTick)args);
        }

        private void OnSummaryProc(object args)
        {
            this.OnSummary?.Invoke(this, (Summary)args);
        }

        private void OnOrderUpdateProc(object args)
        {
            this.OnOrderUpdate?.Invoke(this, (OrderUpdate)args);
        }

        private void OnPositionUpdateProc(object args)
        {
            this.OnPositionUpdate?.Invoke(this, (PositionUpdate)args);
        }

        private void ExecuteEvent(WaitCallback callback, object args)
        {
            ThreadPool.QueueUserWorkItem(delegate (object state)
            {
                try
                {
                    callback(state);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message + " " + ex.StackTrace);
                }
            }, args);
        }
    }
}