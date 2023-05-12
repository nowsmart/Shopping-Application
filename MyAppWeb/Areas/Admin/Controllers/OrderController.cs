﻿using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyApp.CommenHelper;
using MyApp.DataAccessLayer.Infrastructure.IRepository;
using MyApp.Models;
using Stripe;
using Stripe.Checkout;
using System.Security.Claims;

namespace MyAppWeb.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize]
    public class OrderController : Controller
    {
        private IUnitOfWork _unitOfWork;

        public OrderController(IUnitOfWork unitOfWork)
        {
            _unitOfWork = unitOfWork;
        }
        #region APICALL
        public IActionResult AllOrders(string status)
        {
            IEnumerable<OrderHeader> orderHeaders;

            if (User.IsInRole("Admin") || User.IsInRole("Employee"))
            {
                orderHeaders = _unitOfWork.OrderHeader.GetAll(includeProperties: "ApplicationUser");
            }
            else
            {
                var claimsIdentity = (ClaimsIdentity)User.Identity;
                var claims = claimsIdentity.FindFirst(ClaimTypes.NameIdentifier);
                orderHeaders = _unitOfWork.OrderHeader.GetAll(x => x.ApplicationUserId == claims.Value);
            }
            switch (status)
            {
                case "pending":
                    orderHeaders = orderHeaders.Where(x => x.PaymentStatus == PaymentStatus.StatusPending);
                    break;
                case "approved":
                    orderHeaders = orderHeaders.Where(x => x.PaymentStatus == PaymentStatus.StatusApproved);
                    break;
                case "underprocess":
                    orderHeaders = orderHeaders.Where(x => x.OrderStatus == OrderStatus.StatusInProcess);
                    break;
                case "shipped":
                    orderHeaders = orderHeaders.Where(x => x.OrderStatus == OrderStatus.StatusShipped);
                    break;
                default:
                    break;
            }

            return Json(new { data = orderHeaders });
        }
        #endregion
        public IActionResult Index()
        {
            return View();
        }

        public IActionResult OrderDetails(int id)
        {
            OrderVM orderVM = new OrderVM()
            {
                OrderHeader = _unitOfWork.OrderHeader.GetT(x => x.Id == id,
                includeProperties: "ApplicationUser"),
                OrderDetail = _unitOfWork.OrderDetail.GetAll(x => x.Id == id, includeProperties: "Product")
            };
            return View(orderVM);
        }

        [Authorize(Roles = WebSiteRole.Role_Admin + "," + WebSiteRole.Role_Employee)]
        [HttpPost]
        public IActionResult OrderDetails(OrderVM vm)
        {
            OrderHeader orderHeader = _unitOfWork.OrderHeader.GetT(x => x.Id == vm.OrderHeader.Id);
            orderHeader.Name = vm.OrderHeader.Name;
            orderHeader.Phone = vm.OrderHeader.Phone;
            orderHeader.Address = vm.OrderHeader.Address;
            orderHeader.City = vm.OrderHeader.City;
            orderHeader.State = vm.OrderHeader.State;
            orderHeader.PostalCode = vm.OrderHeader.PostalCode;
            if (vm.OrderHeader.Carrier != null)
            {
                orderHeader.Carrier = vm.OrderHeader.Carrier;
            }
            if (vm.OrderHeader.TrackingNumber != null)
            {
                orderHeader.TrackingNumber = vm.OrderHeader.TrackingNumber;
            }
            _unitOfWork.OrderHeader.Update(orderHeader);
            _unitOfWork.Save();
            TempData["success"] = "Info Updated";
            return RedirectToAction("OrderDetails", "Order", new { id = vm.OrderHeader.Id });
        }
        public IActionResult InProcess(OrderVM vm)
        {
            _unitOfWork.OrderHeader.UpdateStatus(vm.OrderHeader.Id, OrderStatus.StatusInProcess);
            _unitOfWork.Save();
            TempData["success"] = "Order Status Updated-InProcess";
            return RedirectToAction("OrderDetails", "Order", new { id = vm.OrderHeader.Id });
        }
        public IActionResult Shipped(OrderVM vm)
        {
            OrderHeader orderHeader = _unitOfWork.OrderHeader.GetT(x => x.Id == vm.OrderHeader.Id);
            orderHeader.Carrier = vm.OrderHeader.Carrier;
            orderHeader.TrackingNumber = vm.OrderHeader.TrackingNumber;
            orderHeader.OrderStatus = OrderStatus.StatusShipped;
            orderHeader.DateOfShipping = DateTime.Now;
            _unitOfWork.OrderHeader.Update(orderHeader);
            _unitOfWork.Save();
            TempData["success"] = "Order Status Updated-Shipped";
            return RedirectToAction("OrderDetails", "Order", new { id = vm.OrderHeader.Id });
        }
        [Authorize(Roles = WebSiteRole.Role_Admin + "," + WebSiteRole.Role_Employee)]
        public IActionResult CancelOrder(OrderVM vm)
        {
            OrderHeader orderHeader = _unitOfWork.OrderHeader.GetT(x => x.Id == vm.OrderHeader.Id);
            if (orderHeader.PaymentStatus == PaymentStatus.StatusApproved)
            {
                var refund = new RefundCreateOptions
                {
                    Reason = RefundReasons.RequestedByCustomer,
                    PaymentIntent = orderHeader.PaymentIntentId
                };
                var service = new RefundService();
                Refund Refund = service.Create(refund);
                _unitOfWork.OrderHeader.UpdateStatus(orderHeader.Id, OrderStatus.StatusCanceled, OrderStatus.StatusRefund);
            }
            else
            {
                _unitOfWork.OrderHeader.UpdateStatus(orderHeader.Id, OrderStatus.StatusCanceled, OrderStatus.StatusCanceled);
            }
            _unitOfWork.Save();
            TempData["success"] = "Order Canceled";
            return RedirectToAction("OrderDetails", "Order", new { id = vm.OrderHeader.Id });
        }
        public IActionResult PayNow(OrderVM vm)
        {
            var OrderHeader = _unitOfWork.OrderHeader.GetT(x => x.Id == vm.OrderHeader.Id, includeProperties: "ApplicationUser");
            var OrderDetail = _unitOfWork.OrderDetail.GetAll(x => x.Id == vm.OrderHeader.Id, includeProperties: "Product");

            foreach (var item in OrderDetail)
            {
                OrderDetail orderDetail = new OrderDetail()
                {
                    ProductId = item.ProductId,
                    OrderHeaderId = vm.OrderHeader.Id,
                    Count = item.Count,
                    Price = item.Product.Price
                };
                _unitOfWork.OrderDetail.Add(orderDetail);
                _unitOfWork.Save();
            }
            var domain = "https://localhost:7284/";
            var options = new SessionCreateOptions
            {
                LineItems = new List<SessionLineItemOptions>(),
                Mode = "payment",
                SuccessUrl = domain + $"Customer/Cart/ordersuccess?id={vm.OrderHeader.Id}",
                CancelUrl = domain + $"Customer/Cart/Index"
            };
            foreach (var item in OrderDetail)
            {
                //long pr = (long)(item.Product.Price*100);
                var lineItemsOptions = new SessionLineItemOptions
                {
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        UnitAmount = (long)(item.Product.Price * 100),
                        Currency = "PKR",
                        ProductData = new SessionLineItemPriceDataProductDataOptions
                        {
                            Name = item.Product.Name,
                        },
                    },
                    Quantity = item.Count,
                };
                options.LineItems.Add(lineItemsOptions);
            }

            var service = new SessionService();
            Session session = service.Create(options);
            _unitOfWork.OrderHeader.PaymentStatus(vm.OrderHeader.Id, session.Id, session.PaymentIntentId);
            _unitOfWork.Save();
            Response.Headers.Add("Location", session.Url);
            return new StatusCodeResult(303);
            return RedirectToAction("Index", "Home");
        }
    }
}