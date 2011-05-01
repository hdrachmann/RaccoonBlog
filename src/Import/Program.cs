﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Xsl;
using Raven.Client;
using Raven.Client.Document;
using RavenDbBlog.Core.Models;
using RavenDbBlog.Infrastructure.EntityFramework;
using Sgml;
using Post = RavenDbBlog.Infrastructure.EntityFramework.Post;
using RavenPost = RavenDbBlog.Core.Models.Post;

namespace RavenDbBlog.Import
{
    internal class Program
    {
        private static readonly HashSet<string> seen = new HashSet<string>();

        private static void Main(string[] args)
        {
            using (var e = new SubtextEntities())
            {
                var comments = e.Comments.ToList();

                foreach (var comment in comments)
                {
                    Console.WriteLine(comment.Id);
                    Convert(comment);
                }
            }



            using (var e = new SubtextEntities())
            {
                Console.WriteLine("Starting...");

                Stopwatch sp = Stopwatch.StartNew();
                IOrderedEnumerable<Post> theEntireDatabaseOhMygod = e.Posts
                    .Include("Comments")
                    .Include("Links")
                    .Include("Links.Categories")
                    .ToList()
                    .OrderBy(x => x.DateSyndicated);

                Console.WriteLine("Loading data took {0:#,#} ms", sp.ElapsedMilliseconds);


                using (IDocumentStore store = new DocumentStore
                    {
                        Url = "http://localhost:8080",
                    }.Initialize())
                {
                    using (IDocumentSession s = store.OpenSession())
                    {
                        var users = new[]
                            {
                                new {Email = "ayende@ayende.com", FullName = "Ayende Rahien"},
                                new {Email = "fitzchak@ayende.com", FullName = "Fitzchak Yitzchaki"},
                            };

                        for (int i = 0; i < users.Length; i++)
                        {
                            var user = new User
                                {
                                    Id = "users/" + (i + 1),
                                    Email = users[i].Email,
                                    FullName = users[i].FullName,
                                    Enabled = true,
                                };
                            user.SetPassword("123456");
                            s.Store(user);
                        }
                        s.SaveChanges();
                    }

                    foreach (Post post in theEntireDatabaseOhMygod)
                    {
                        var ravenPost = new RavenPost
                            {
                                Author = post.Author,
                                CreatedAt = new DateTimeOffset(post.DateAdded),
                                PublishAt = new DateTimeOffset(post.DateSyndicated ?? post.DateAdded),
                                Body = post.Text,
                                CommentsCount = post.FeedBackCount,
                                LegacySlug = post.EntryName,
                                Title = post.Title,
                                Tags = post.Links.Select(x => x.Categories.Title)
                                    .Where(x => x != "Uncategorized")
                                    .ToArray()
                            };

                        var commentsCollection = new PostComments();
                        commentsCollection.Comments = post.Comments
                            .Where(comment => comment.StatusFlag == 1)
                            .OrderBy(comment => comment.DateCreated)
                            .Select(
                                comment => new PostComments.Comment
                                    {
                                        Id = commentsCollection.GenerateNewCommentId(),
                                        Author = comment.Author,
                                        Body = ConvertCommentToMarkdown(comment.Body),
                                        CreatedAt = comment.DateCreated,
                                        Email = comment.Email,
                                        Important = comment.IsBlogAuthor ?? false,
                                        Url = comment.Url,
                                        IsSpam = false
                                    }
                            ).ToList();
                        commentsCollection.Spam = post.Comments
                            .Where(comment => comment.StatusFlag != 1)
                            .OrderBy(comment => comment.DateCreated)
                            .Select(
                                comment => new PostComments.Comment
                                    {
                                        Id = commentsCollection.GenerateNewCommentId(),
                                        Author = comment.Author,
                                        Body = ConvertCommentToMarkdown(comment.Body),
                                        CreatedAt = comment.DateCreated,
                                        Email = comment.Email,
                                        Important = comment.IsBlogAuthor ?? false,
                                        Url = comment.Url,
                                        IsSpam = true
                                    }
                            ).ToList();

                        using (IDocumentSession session = store.OpenSession())
                        {
                            session.Store(commentsCollection);
                            ravenPost.CommentsId = commentsCollection.Id;

                            session.Store(ravenPost);

                            session.SaveChanges();
                        }
                    }
                }

                Console.WriteLine(sp.Elapsed);
            }
        }

        private static string Convert(Comment comment)
        {
            var sb = new StringBuilder();

            var sgmlReader = new SgmlReader
                {
                    InputStream = new StringReader(comment.Body),
                    DocType = "HTML",
                    WhitespaceHandling = WhitespaceHandling.Significant,
                    CaseFolding = CaseFolding.ToLower
                };

            bool outputEndElement = false;
            int indentLevel = 0;
            while (sgmlReader.Read())
            {
                switch (sgmlReader.NodeType)
                {
                    case XmlNodeType.Text:
                        if (indentLevel > 0)
                            sb.Append("\t");
                        sb.AppendLine(sgmlReader.Value);
                        break;
                    case XmlNodeType.Element:
                        switch (sgmlReader.LocalName)
                        {
                            case "h1":
                                sb.Append("## ");
                                break;
                            case "br":
                                sb.AppendLine("  ");
                                break;
                            case "a":
                                if (sgmlReader.MoveToAttribute("href"))
                                {
                                    string url = sgmlReader.Value;
                                    sgmlReader.Read();

                                    sb.AppendFormat("[{0}]({1})", sgmlReader.Value, url);
                                }
                                break;
                            case "html":
                                break;
                            case "pre":
                            case "code":
                            case "quote":
                                indentLevel = 1;
                                break;
                            default:
                                Console.WriteLine(sgmlReader.LocalName);
                                outputEndElement = true;
                                sb.Append("<").Append(sgmlReader.LocalName);
                                break;
                        }
                        break;
                    case XmlNodeType.SignificantWhitespace:
                    case XmlNodeType.Whitespace:
                        break;
                    case XmlNodeType.EndElement:
                        indentLevel = 0;
                        if (outputEndElement)
                            sb.Append(">");
                        outputEndElement = false;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            return sb.ToString();
        }

        private static string ConvertCommentToMarkdown(string body)
        {

            body = body.Replace("<br />", "  " + Environment.NewLine);

            body = body.Replace("<strong>", "**");
            body = body.Replace("</strong>", "**");
            body = body.Replace("<b>", "**");
            body = body.Replace("</b>", "**");

            body = body.Replace("<i>", "*");
            body = body.Replace("</i>", "*");
            body = body.Replace("<em>", "*");
            body = body.Replace("</em>", "*");

            body = body.Replace("<h1>", "# ");
            body = body.Replace("</h1>", "");


            return body;
        }

    }
}