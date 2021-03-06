﻿using System;
using System.Collections.Generic;
using System.Linq;

using Stove.Dapper.Repositories;
using Stove.Demo.ConsoleApp.Entities;
using Stove.Domain.Repositories;
using Stove.Domain.Services;
using Stove.Domain.Uow;
using Stove.Runtime.Session;

namespace Stove.Demo.ConsoleApp
{
    public class ProductDomainService : DomainService
    {
        private readonly IDapperRepository<Mail, Guid> _mailDapperRepository;
        private readonly IDapperRepository<Product> _productDapperRepository;
        private readonly IRepository<Product> _productRepository;
        private readonly IRepository<Animal> _animalRepository;
        private readonly IDapperRepository<Animal> _animalDapperRepository;

        public ProductDomainService(
            IDapperRepository<Product> productDapperRepository,
            IDapperRepository<Mail, Guid> mailDapperRepository,
            IRepository<Product> productRepository,
            IDapperRepository<Animal> animalDapperRepository,
            IRepository<Animal> animalRepository)
        {
            _productDapperRepository = productDapperRepository;
            _mailDapperRepository = mailDapperRepository;
            _productRepository = productRepository;
            _animalDapperRepository = animalDapperRepository;
            _animalRepository = animalRepository;
        }

        public IStoveSession StoveSession { get; set; }

        public void DoSomeStuff()
        {
            using (IUnitOfWorkCompleteHandle uow = UnitOfWorkManager.Begin())
            {
                using (StoveSession.Use(266))
                {
                    _productDapperRepository.Insert(new Product("TShirt"));
                    int gomlekId = _productDapperRepository.InsertAndGetId(new Product("Gomlek"));

                    Product gomlekFromEf = _productRepository.FirstOrDefault(gomlekId);

                    _productDapperRepository.Execute("update products set Name = @name where Id = @id", new { name = "TShirt_From_ExecuteQuery", @id = gomlekId });

                    Product firstProduct = _productDapperRepository.Get(gomlekId);

                    _animalRepository.InsertAndGetId(new Animal("Bird"));

                    _animalDapperRepository.GetAll(x => x.Name == "Bird");

                    _productDapperRepository.GetAll(x => x.Id == 1 || x.Name == "Gomlek" || x.CreationTime == DateTime.Now);

                    _productDapperRepository.GetAll(x => (x.Id == 1 && x.Name == "Gomlek") || x.CreationTime == DateTime.Now);

                    _productDapperRepository.GetAll(x => ((x.Id == 1 || x.Name == "Gomlek") && x.CreationTime == DateTime.Now) ||
                     (x.Id == 1 || x.Name == "Gomlek") && x.CreationTime == DateTime.Now);

                    IEnumerable<Product> products = _productDapperRepository.GetAll();

                    firstProduct.Name = "Something";

                    _productDapperRepository.Update(firstProduct);

                    _mailDapperRepository.Insert(new Mail("New Product Added"));
                    Guid mailId = _mailDapperRepository.InsertAndGetId(new Mail("Second Product Added"));

                    IEnumerable<Mail> mails = _mailDapperRepository.GetAll();

                    Mail firstMail = mails.First();

                    firstMail.Subject = "Sorry wrong email!";

                    _mailDapperRepository.Update(firstMail);
                }

                uow.Complete();
            }
        }
    }
}
