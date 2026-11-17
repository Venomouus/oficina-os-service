Feature: Saga distribuida de ponta a ponta
  Os tres microsservicos devem trocar eventos reais pelo RabbitMQ.

  Scenario: Pagamento simulado libera a execucao e finaliza a OS
    Given uma ordem de servico nova com orcamento criado
    When o orcamento e aprovado e o pagamento sandbox e confirmado
    Then a ordem deve chegar a aguardando execucao
    When a execucao e finalizada
    Then a ordem deve chegar a finalizada
    And o orcamento deve continuar pago
    And o historico da ordem deve registrar quatro transicoes

  Scenario: Falha na execucao compensa a OS sem desfazer pagamento capturado
    Given uma ordem de servico nova com orcamento criado
    When o orcamento e aprovado e o pagamento sandbox e confirmado
    Then a ordem deve chegar a aguardando execucao
    When a execucao falha
    Then a ordem deve chegar a erro de execucao
    And o orcamento deve continuar pago
    And o historico da ordem deve registrar quatro transicoes
